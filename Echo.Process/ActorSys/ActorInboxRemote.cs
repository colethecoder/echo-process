﻿using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using static Echo.Process;
using static LanguageExt.Prelude;
using Echo.ActorSys;
using LanguageExt;

namespace Echo
{
    class ActorInboxRemote<S,T> : IActorInbox
    {
        ICluster cluster;
        PausableBlockingQueue<string> userNotify;
        Actor<S, T> actor;
        ActorItem parent;
        int maxMailboxSize;
        int scheduledItems;
        object sync = new object();

        public Unit Startup(IActor process, ActorItem parent, Option<ICluster> cluster, int maxMailboxSize)
        {
            if (cluster.IsNone) throw new Exception("Remote inboxes not supported when there's no cluster");
            this.actor = (Actor<S, T>)process;
            this.cluster = cluster.IfNoneUnsafe(() => null);

            this.maxMailboxSize = maxMailboxSize == -1
                ? ActorContext.System(actor.Id).Settings.GetProcessMailboxSize(actor.Id)
                : maxMailboxSize;

            this.parent = parent;

            var obj = new ThreadObj { Actor = actor, Inbox = this, Parent = parent };
            userNotify = new PausableChannel<string>(this.maxMailboxSize, async msg => { 
                await CheckRemoteInbox(ActorInboxCommon.ClusterUserInboxKey(state.Actor.Id), true); 
                return InboxDirective.Default; });

            userNotify.ReceiveAsync(obj, (state, msg) => { CheckRemoteInbox(ActorInboxCommon.ClusterUserInboxKey(state.Actor.Id), true); return InboxDirective.Default; });

            SubscribeToSysInboxChannel();
            SubscribeToUserInboxChannel();

            this.cluster.SetValue(ActorInboxCommon.ClusterMetaDataKey(actor.Id), new ProcessMetaData(
                new[] { typeof(T).AssemblyQualifiedName },
                typeof(S).AssemblyQualifiedName,
                typeof(S).GetTypeInfo().ImplementedInterfaces.Map(x => x.AssemblyQualifiedName).ToArray()
                ));

            return unit;
        }

        class ThreadObj
        {
            public Actor<S, T> Actor;
            public ActorInboxRemote<S, T> Inbox;
            public ActorItem Parent;
        }

        int MailboxSize =>
            maxMailboxSize < 0
                ? ActorContext.System(actor.Id).Settings.GetProcessMailboxSize(actor.Id)
                : maxMailboxSize;

        void SubscribeToSysInboxChannel()
        {
            // System inbox is just listening to the notifications, that means that system
            // messages don't persist.
            cluster.UnsubscribeChannel(ActorInboxCommon.ClusterSystemInboxNotifyKey(actor.Id));
            cluster.SubscribeToChannel<RemoteMessageDTO>(ActorInboxCommon.ClusterSystemInboxNotifyKey(actor.Id)).Subscribe(SysInbox);
        }

        void SubscribeToUserInboxChannel()
        {
            cluster.UnsubscribeChannel(ActorInboxCommon.ClusterUserInboxNotifyKey(actor.Id));
            cluster.SubscribeToChannel<string>(ActorInboxCommon.ClusterUserInboxNotifyKey(actor.Id)).Subscribe(msg => userNotify.Post(msg));
            // We want the check done asyncronously, in case the setup function creates child processes that
            // won't exist if we invoke directly.
            cluster.PublishToChannel(ActorInboxCommon.ClusterUserInboxNotifyKey(actor.Id), Guid.NewGuid().ToString());
        }

        void SubscribeToScheduleInboxChannel()
        {
            cluster.UnsubscribeChannel(ActorInboxCommon.ClusterScheduleNotifyKey(actor.Id));
            cluster.SubscribeToChannel<string>(ActorInboxCommon.ClusterScheduleNotifyKey(actor.Id)).Subscribe(msg => scheduledItems++);
            // We want the check done asyncronously, in case the setup function creates child processes that
            // won't exist if we invoke directly.

            // TODO: Consider the implications of race condition here --- will probably need some large 'clear out' process that does a query
            //       on the cluster.  Or maybe this internal counter isn't the best approach.
            scheduledItems = cluster.GetHashFields(ActorInboxCommon.ClusterScheduleKey(actor.Id)).Count; 
        }

        public bool IsPaused
        {
            get;
            private set;
        }

        public Unit Pause()
        {
            lock (sync)
            {
                if (!IsPaused)
                {
                    IsPaused = true;
                    cluster?.UnsubscribeChannel(ActorInboxCommon.ClusterUserInboxNotifyKey(actor.Id));
                }
            }
            return unit;
        }

        public Unit Unpause()
        {
            lock (sync)
            {
                if (IsPaused)
                {
                    IsPaused = false;
                    SubscribeToUserInboxChannel();
                }
            }
            return unit;
        }

        /// <summary>
        /// TODO: This is a combination of code in ActorCommon.GetNextMessage and
        ///       CheckRemoteInbox.  Some factoring is needed.
        /// </summary>
        void SysInbox(RemoteMessageDTO dto)
        {
            try
            {
                if (dto == null)
                {
                    // Failed to deserialise properly
                    return;
                }
                if (dto.Tag == 0 && dto.Type == 0)
                {
                    // Message is bad
                    tell(ActorContext.System(actor.Id).DeadLetters, DeadLetter.create(dto.Sender, actor.Id, null, "Failed to deserialise message: ", dto));
                    return;
                }
                var msg = MessageSerialiser.DeserialiseMsg(dto, actor.Id);

                try
                {
                    lock(sync)
                    {
                        ActorInboxCommon.SystemMessageInbox(actor, this, (SystemMessage)msg, parent);
                    }
                }
                catch (Exception e)
                {
                    var session = msg.SessionId == null
                        ? None
                        : Some(new SessionId(msg.SessionId));

                    ActorContext.System(actor.Id).WithContext(new ActorItem(actor, this, actor.Flags), parent, dto.Sender, msg as ActorRequest, msg, session, () => replyErrorIfAsked(e));
                    tell(ActorContext.System(actor.Id).DeadLetters, DeadLetter.create(dto.Sender, actor.Id, e, "Remote message inbox.", msg));
                    logSysErr(e);
                }
            }
            catch (Exception e)
            {
                logSysErr(e);
            }
        }

        async ValueTask CheckRemoteInbox(string key, bool pausable)
        {
            var inbox = this;
            var count = cluster?.QueueLength(key) ?? 0;

            while (count > 0 && (!pausable || !IsPaused))
            {
                var directive = InboxDirective.Default;

                ActorInboxCommon.GetNextMessage(cluster, actor.Id, key).IfSome(
                    x => iter(x, (dto, msg) =>
                    {
                        try
                        {
                            switch (msg.MessageType)
                            {
                                case Message.Type.User:        directive = await ActorInboxCommon.UserMessageInbox(actor, inbox, (UserControlMessage)msg, parent); break;
                                case Message.Type.UserControl: directive = await ActorInboxCommon.UserMessageInbox(actor, inbox, (UserControlMessage)msg, parent); break;
                            }
                        }
                        catch (Exception e)
                        {
                            var session = msg.SessionId == null
                                ? None
                                : Some(new SessionId(msg.SessionId));

                            ActorContext.System(actor.Id).WithContext(new ActorItem(actor, inbox, actor.Flags), parent, dto.Sender, msg as ActorRequest, msg, session, () => replyErrorIfAsked(e));
                            tell(ActorContext.System(actor.Id).DeadLetters, DeadLetter.create(dto.Sender, actor.Id, e, "Remote message inbox.", msg));
                            logSysErr(e);
                        }
                        finally
                        {
                            if ((directive & InboxDirective.Pause) != 0)
                            {
                                IsPaused = true;
                                directive = directive & (~InboxDirective.Pause);
                            }

                            if (directive == InboxDirective.Default)
                            {
                                cluster?.Dequeue<RemoteMessageDTO>(key);
                            }
                        }
                    }));

                if (directive == InboxDirective.Default)
                {
                    count--;
                }
            }
        }

        public Unit Shutdown()
        {
            Dispose();
            return unit;
        }

        public void Dispose()
        {
            //tokenSource?.Cancel();
            //tokenSource?.Dispose();
            //tokenSource = null;

            userNotify.Cancel();

            try { cluster?.UnsubscribeChannel(ActorInboxCommon.ClusterUserInboxNotifyKey(actor.Id)); } catch { };
            try { cluster?.UnsubscribeChannel(ActorInboxCommon.ClusterSystemInboxNotifyKey(actor.Id)); } catch { };
            cluster = null;
        }
    }
}

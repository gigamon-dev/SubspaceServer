using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Utilities;
using System.Text;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that manages matchmaking queues.
    /// </summary>
    /// <remarks>
    /// This module provides the ?next command for adding oneself or group to a matchmaking queue.
    /// Likewise, it also provides the ?cancel command to remove oneself or group from the queue.
    /// Other modules register queues through the <see cref="IMatchmakingQueues"/> interface.
    /// </remarks>
    public class MatchmakingQueues : IModule, IMatchmakingQueues, IPlayerGroupAdvisor
    {
        private ComponentBroker _broker;

        // required dependencies
        private IChat _chat;
        private ICapabilityManager _capabilityManager;
        private ICommandManager _commandManager;
        private ILogManager _logManager;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IPlayerGroups _playerGroups;

        // optional dependencies
        private IHelp _help;

        private InterfaceRegistrationToken<IMatchmakingQueues> _iMatchmakingQueuesToken;

        private PlayerDataKey<UsageData> _pdKey;

        private readonly Dictionary<string, IMatchmakingQueue> _queues = new(16, StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<IPlayerGroup, UsageData> _groupUsageDictionary = new(128);
        private readonly ObjectPool<UsageData> _usageDataPool = new NonTransientObjectPool<UsageData>(new QueueUsageDataPooledObjectPolicy()); // only for groups TODO: add a way to use the same pool as per-player data

        private const string NextCommandName = "next";
        private const string CancelCommandName = "cancel";

        #region Module members

        public bool Load(
            ComponentBroker broker,
            ICapabilityManager capabilityManager,
            IChat chat,
            ICommandManager commandManager,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IPlayerGroups playerGroups)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _playerGroups = playerGroups ?? throw new ArgumentNullException(nameof(playerGroups));

            _help = broker.GetInterface<IHelp>();

            _pdKey = _playerData.AllocatePlayerData(new QueueUsageDataPooledObjectPolicy());

            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            PlayerGroupMemberLeavingCallback.Register(broker, Callback_PlayerGroupMemberLeaving);
            PlayerGroupDisbandedCallback.Register(broker, Callback_PlayerGroupDisbanded);

            _commandManager.AddCommand(NextCommandName, Command_next);
            _commandManager.AddCommand(CancelCommandName, Command_cancel);

            _iMatchmakingQueuesToken = broker.RegisterInterface<IMatchmakingQueues>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface(ref _iMatchmakingQueuesToken);

            _commandManager.RemoveCommand(NextCommandName, Command_next);
            _commandManager.RemoveCommand(CancelCommandName, Command_cancel);

            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            PlayerGroupMemberLeavingCallback.Unregister(broker, Callback_PlayerGroupMemberLeaving);
            PlayerGroupDisbandedCallback.Unregister(broker, Callback_PlayerGroupDisbanded);

            _playerData.FreePlayerData(_pdKey);

            if (_help != null)
            {
                broker.ReleaseInterface(ref _help);
            }

            return true;
        }

        #endregion

        #region IMatchmakingQueues

        bool IMatchmakingQueues.RegisterQueue(IMatchmakingQueue queue)
        {
            if (queue == null)
                return false;

            if (!queue.Options.AllowSolo && !queue.Options.AllowGroups)
            {
                _logManager.LogM(LogLevel.Error, nameof(MatchmakingQueues), $"Queue '{queue.Name}' cannot be added because it was not set to allow adding solo or grouped players.");
                return false;
            }

            return _queues.TryAdd(queue.Name, queue);
        }

        bool IMatchmakingQueues.UnregisterQueue(IMatchmakingQueue queue)
        {
            if (queue == null)
                return false;

            // TODO: if any player was queued, dequeue. If it was their last queue, change their status.

            if (!_queues.Remove(queue.Name, out IMatchmakingQueue removedQueue))
                return false;

            if (queue != removedQueue)
            {
                _queues.Add(removedQueue.Name, removedQueue);
                return false;
            }

            //
            // Clear the queue
            //

            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
            HashSet<IPlayerGroup> groups = new(); // TODO: pooling

            try
            {
                removedQueue.GetQueued(players, groups);

                foreach (Player player in players)
                {
                    removedQueue.Remove(player);

                    if (!player.TryGetExtraData(_pdKey, out UsageData usageData))
                        continue;

                    usageData.RemoveQueue(removedQueue);
                }

                foreach (IPlayerGroup group in groups)
                {
                    removedQueue.Remove(group);

                    if (!_groupUsageDictionary.TryGetValue(group, out UsageData usageData))
                        continue;

                    usageData.RemoveQueue(removedQueue);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(players);
            }

            return true;
        }

        #endregion

        #region IPlayerGroupAdvisor

        bool IPlayerGroupAdvisor.AllowSendInvite(IPlayerGroup group, StringBuilder message)
        {
            if (group == null || !_groupUsageDictionary.TryGetValue(group, out UsageData usageData))
                return true;

            if (usageData.Queues.Count == 0)
                return true; // Can invite if not searching for a match.

            message?.Append("Cannot invite while searching for a match. To invite, stop the search first.");
            return false;
        }

        bool IPlayerGroupAdvisor.AllowAcceptInvite(Player player, StringBuilder message)
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out UsageData usageData))
                return true;

            if (usageData.Queues.Count == 0)
                return true;

            message?.Append("Cannot accept an invite while searching for a match. To accept, stop the search first.");
            return false;
        }

        #endregion

        #region Callbacks

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.Disconnect)
            {
                // Remove the player from queues or groups.
                // If the player was in a group that is currently queued, make sure that group gets dequeued.
                if (!player.TryGetExtraData(_pdKey, out UsageData usageData))
                    return;

                if (usageData.State == QueueState.Queued)
                {
                    //if (pd.Group != null)
                    {
                        // TODO: Remove the group from the queue(s).
                        //pd.Group.Queues

                        // Remove the player from the group.
                        //RemoveMember(pd.Group, player);
                    }
                    //else
                    {
                        // TODO: Remove the player from the queue(s).
                        //pd.Queues
                    }
                }
            }
        }

        private void Callback_PlayerGroupMemberLeaving(IPlayerGroup group, Player player)
        {
            if (!_groupUsageDictionary.TryGetValue(group, out UsageData usageData))
                return;

            if (usageData.Queues.Count > 0)
            {
                // The group is searching for a match, stop the search.
                foreach (var queue in usageData.Queues)
                {
                    queue.Remove(group);
                }
                usageData.RemoveAllQueues();

                // Notify the members.
                foreach (Player member in group.Members)
                {
                    _chat.SendMessage(member, $"{NextCommandName}: Search stopped.");
                }
            }
        }

        private void Callback_PlayerGroupDisbanded(IPlayerGroup group)
        {
            if (_groupUsageDictionary.Remove(group, out UsageData usageData))
            {
                _usageDataPool.Return(usageData);
            }
        }

        #endregion

        #region Commands

        // ?next --> add the player to the current arena's default queue if there is one
        // /?next --> mods can see queue info for another player
        // ?next 1v1
        // ?next 2v2
        // ?next 3v3
        // ?next 4v4
        // ?next pb
        // ?next pb3h
        // ?next pbmini
        // ?next 1v1,2v2,3v3,4v4
        // ?next pb3h,pbmini
        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[<none> | <queue name>[, <queue name>[, ...]]]",
            Description = 
            "Starts a matchmaking search.\n" +
            "An arena may be configured with a default search queue, in which case, specifying a <queue name> is not necessary.")]
        private void Command_next(string commandName, string parameters, Player player, ITarget target)
        {
            if (player.Status != PlayerState.Playing || player.Arena == null)
                return;

            // Check if the player is in a group.
            IPlayerGroup group = _playerGroups.GetGroup(player);

            // Get the usage data.
            UsageData usageData;
            if (group != null)
            {
                if (!_groupUsageDictionary.TryGetValue(group, out usageData))
                {
                    usageData = _usageDataPool.Get();
                    _groupUsageDictionary.Add(group, usageData);
                }
            }
            else
            {
                if (!player.TryGetExtraData(_pdKey, out usageData))
                    return;
            }

            if (string.Equals(parameters, "-list"))
            {
                // Print usage details.
                switch (usageData.State)
                {
                    case QueueState.None:
                        _chat.SendMessage(player, $"{(group == null ? "You are" : "Your group is")} not searching for a game yet.");
                        break;

                    case QueueState.Queued:
                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                        try
                        {
                            foreach (var queue in usageData.Queues)
                            {
                                if (sb.Length > 0)
                                    sb.Append(", ");

                                sb.Append(queue.Name);
                            }

                            _chat.SendMessage(player, $"{(group == null ? "You are" : "Your group is")} searching for a game on the following queues: {sb}");
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }
                        break;

                    case QueueState.Playing:
                        _chat.SendMessage(player, $"{(group == null ? "You are" : "Your group is")} currently playing in match.");
                        break;

                    default:
                        break;
                }

                return;
            }

            // The command is to start a search.

            if (group != null && player != group.Leader)
            {
                _chat.SendMessage(player, $"{NextCommandName}: Only the group leader can start a search.");
                return;
            }

            if (usageData.State == QueueState.Playing)
            {
                _chat.SendMessage(player, $"{NextCommandName}: Can't start a search while playing in a match.");
                return;
            }

            string queueName = null;

            if (string.IsNullOrWhiteSpace(parameters))
            {
                // No queue name(s) were specified. Check if there is a default queue for the arena.
                var advisors = _broker.GetAdvisors<IMatchmakingQueueAdvisor>();
                foreach (var advisor in advisors)
                {
                    queueName = advisor.GetDefaultQueue(player.Arena);
                    if (queueName != null)
                    {
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(queueName))
                {
                    _chat.SendMessage(player, $"{NextCommandName}: You must specify which queue(s) to search on.");
                    return;
                }

                AddToQueue(player, group, usageData, queueName);
            }
            else
            {
                ReadOnlySpan<char> remaining = parameters;
                ReadOnlySpan<char> token;
                while ((token = remaining.GetToken(", ", out remaining)).Length > 0)
                {
                    queueName = token.ToString(); // FIXME: string allocation needed to search dictionary

                    AddToQueue(player, group, usageData, queueName);
                }
            }

            void AddToQueue(Player player, IPlayerGroup group, UsageData usageData, string queueName)
            {
                Arena arena = player.Arena;
                IMatchmakingQueue queue = null;

                if (!_queues.TryGetValue(queueName, out queue))
                {
                    // Did not find a queue with the name provided.
                    // Check if the name provided is an alias.
                    var advisors = arena.GetAdvisors<IMatchmakingQueueAdvisor>();
                    foreach (IMatchmakingQueueAdvisor advisor in advisors)
                    {
                        string alias = advisor.GetQueueNameByAlias(arena, queueName);
                        if (alias != null && _queues.TryGetValue(alias, out queue))
                        {
                            queueName = alias;
                            break;
                        }
                    }
                }

                if (queue == null)
                {
                    _chat.SendMessage(player, $"{NextCommandName}: Queue '{queueName}' not found.");
                    return;
                }

                //
                // Add to queue
                //

                if (group != null)
                {
                    // group search
                    if (!queue.Options.AllowGroups)
                    {
                        _chat.SendMessage(player, $"{NextCommandName}: Queue '{queueName}' does not allow premade groups.");
                        return;
                    }

                    if (group.Members.Count < queue.Options.MinGroupSize
                        || group.Members.Count > queue.Options.MaxGroupSize)
                    {
                        if (queue.Options.MinGroupSize == queue.Options.MaxGroupSize)
                            _chat.SendMessage(player, $"{NextCommandName}: Queue '{queueName}' allows groups with exactly {queue.Options.MinGroupSize} players, but your group has {group.Members.Count} players.");
                        else
                            _chat.SendMessage(player, $"{NextCommandName}: Queue '{queueName}' allows groups sized from {queue.Options.MinGroupSize} to {queue.Options.MaxGroupSize} players, but your group has {group.Members.Count} players.");

                        return;
                    }

                    queue.Add(group);
                    usageData.AddQueue(queue);

                    // Notify the group members that a search has started.
                    HashSet<Player> members = _objectPoolManager.PlayerSetPool.Get();
                    try
                    {
                        members.UnionWith(group.Members);
                        _chat.SendSetMessage(members, $"{NextCommandName}: Started searching for a game on queue '{queueName}'.");
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(members);
                    }

                    MatchmakingQueueChangedCallback.Fire(_broker, queue, QueueAction.Add, QueueItemType.Group);
                }
                else
                {
                    // solo search
                    if (!queue.Options.AllowSolo)
                    {
                        _chat.SendMessage(player, $"{NextCommandName}: Queue '{queueName}' does not allow solo play. Create or join a group first.");
                        return;
                    }

                    queue.Add(player);
                    usageData.AddQueue(queue);

                    // Notify the player that a search has started.
                    _chat.SendMessage(player, $"{NextCommandName}: Started searching for a game on queue '{queueName}'.");

                    MatchmakingQueueChangedCallback.Fire(_broker, queue, QueueAction.Add, QueueItemType.Player);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<none> | [<queue name>[, <queue name>[, ...]]]",
            Description = "Cancels a matchmaking search.\n" +
            "Use the command without specifying a <queue name> to remove from all matchmaking queues.\n" +
            "An arena may be configured with a default search queue, in which case, specifying a <queue name> is not necessary.")]
        private void Command_cancel(string commandName, string parameters, Player player, ITarget target)
        {
            // Check if the player is in a group.
            IPlayerGroup group = _playerGroups.GetGroup(player);

            // Get the usage data.
            UsageData usageData;
            if (group != null)
            {
                if (player != group.Leader)
                {
                    _chat.SendMessage(player, $"{NextCommandName}: Only the group leader can cancel a search.");
                    return;
                }

                if (!_groupUsageDictionary.TryGetValue(group, out usageData))
                {
                    usageData = _usageDataPool.Get();
                    _groupUsageDictionary.Add(group, usageData);
                }
            }
            else
            {
                if (!player.TryGetExtraData(_pdKey, out usageData))
                    return;
            }

            if (string.IsNullOrWhiteSpace(parameters))
            {
                // Remove from all queues.
                foreach (var queue in usageData.Queues)
                {
                    if (group != null)
                        queue.Remove(group);
                    else
                        queue.Remove(player);

                    // TODO: use a temporary collection so that the callback can be fired this at the end instead
                    MatchmakingQueueChangedCallback.Fire(_broker, queue, QueueAction.Remove, group != null ? QueueItemType.Group : QueueItemType.Player);
                }

                usageData.RemoveAllQueues();

                // Notify
                if (group != null)
                {
                    HashSet<Player> members = _objectPoolManager.PlayerSetPool.Get();
                    try
                    {
                        members.UnionWith(group.Members);
                        _chat.SendSetMessage(members, $"{NextCommandName}: Search stopped.");
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(members);
                    }
                }
                else
                {
                    _chat.SendMessage(player, $"{NextCommandName}: Search stopped.");
                }

                return;
            }
            else
            {
                ReadOnlySpan<char> remaining = parameters;
                ReadOnlySpan<char> token;
                while ((token = remaining.GetToken(", ", out remaining)).Length > 0)
                {
                    string queueName = token.ToString(); // FIXME: string allocation needed to search dictionary

                    if (!_queues.TryGetValue(queueName, out IMatchmakingQueue queue))
                    {
                        // Did not find a queue with the name provided.
                        // Check if the name provided is an alias.
                        Arena arena = player.Arena;
                        if (arena != null)
                        {
                            var advisors = arena.GetAdvisors<IMatchmakingQueueAdvisor>();
                            foreach (IMatchmakingQueueAdvisor advisor in advisors)
                            {
                                string alias = advisor.GetQueueNameByAlias(arena, queueName);
                                if (alias != null && _queues.TryGetValue(alias, out queue))
                                {
                                    queueName = alias;
                                    break;
                                }
                            }
                        }
                    }

                    if (queue == null)
                    {
                        _chat.SendMessage(player, $"{NextCommandName}: Queue '{queueName}' not found.");
                        continue;
                    }

                    if (group != null)
                        queue.Remove(group);
                    else
                        queue.Remove(player);

                    usageData.RemoveQueue(queue);

                    // Notify
                    if (group != null)
                    {
                        HashSet<Player> members = _objectPoolManager.PlayerSetPool.Get();
                        try
                        {
                            members.UnionWith(group.Members);
                            _chat.SendSetMessage(members, $"{NextCommandName}: Search stopped on queue: {queue.Name}.");
                        }
                        finally
                        {
                            _objectPoolManager.PlayerSetPool.Return(members);
                        }
                    }
                    else
                    {
                        _chat.SendMessage(player, $"{NextCommandName}: Search stopped on queue: {queue.Name}.");
                    }

                    MatchmakingQueueChangedCallback.Fire(_broker, queue, QueueAction.Remove, group != null ? QueueItemType.Group : QueueItemType.Player);
                }
            }
        }

        #endregion

        private void RemoveFromQueue(IMatchmakingQueue queue, Player player)
        {
            if (!queue.Remove(player))
                return;

            if (!player.TryGetExtraData(_pdKey, out UsageData usageData))
                return;

            usageData.RemoveQueue(queue);
        }

        private void RemoveFromQueue(IMatchmakingQueue queue, IPlayerGroup group)
        {
            if (!queue.Remove(group))
                return;

            if (!_groupUsageDictionary.TryGetValue(group, out UsageData usageData))
                return;

            usageData.RemoveQueue(queue);
        }

        private enum QueueState
        {
            None,

            /// <summary>
            /// Searching for a match.
            /// </summary>
            Queued,

            /// <summary>
            /// Playing in a match.
            /// </summary>
            Playing,
        }

        private class UsageData
        {
            public QueueState State { get; private set; }
            public bool AutoRequeue;

            private readonly HashSet<IMatchmakingQueue> _queues = new();
            public IReadOnlySet<IMatchmakingQueue> Queues => _queues;

            public bool AddQueue(IMatchmakingQueue queue)
            {
                if (!_queues.Add(queue))
                    return false;

                if (State == QueueState.None)
                    State = QueueState.Queued;

                return true;
            }

            public bool RemoveQueue(IMatchmakingQueue queue)
            {
                if (!_queues.Remove(queue))
                    return false;

                if (State == QueueState.Queued && _queues.Count == 0)
                    State = QueueState.None;

                return true;
            }

            public bool RemoveAllQueues()
            {
                if (_queues.Count == 0)
                    return false;

                _queues.Clear();

                if (State == QueueState.Queued)
                    State = QueueState.None;

                return true;
            }

            public void Reset()
            {
                State = QueueState.None;
                _queues.Clear();
                AutoRequeue = false;
            }
        }

        private class QueueUsageDataPooledObjectPolicy : IPooledObjectPolicy<UsageData>
        {
            public UsageData Create()
            {
                return new UsageData();
            }

            public bool Return(UsageData obj)
            {
                if (obj == null)
                    return false;

                obj.Reset();

                return true;
            }
        }
    }
}

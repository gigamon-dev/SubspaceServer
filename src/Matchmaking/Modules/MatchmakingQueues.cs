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
        private readonly ObjectPool<List<IMatchmakingQueue>> _iMatchmakingQueueListPool = new DefaultObjectPool<List<IMatchmakingQueue>>(new IMatchmakingQueueListPooledObjectPolicy());
        private readonly ObjectPool<List<PlayerOrGroup>> _playerOrGroupListPool = new DefaultObjectPool<List<PlayerOrGroup>>(new PlayerOrGroupListPooledObjectPolicy());

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
            PlayerGroupMemberRemovedCallback.Register(broker, Callback_PlayerGroupMemberRemoved);
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
            PlayerGroupMemberRemovedCallback.Unregister(broker, Callback_PlayerGroupMemberRemoved);
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

        void IMatchmakingQueues.SetPlaying(HashSet<Player> soloPlayers, HashSet<IPlayerGroup> groups)
        {
            if (soloPlayers != null)
            {
                foreach (Player player in soloPlayers)
                {
                    if (!player.TryGetExtraData(_pdKey, out UsageData usageData))
                        continue;

                    foreach (IMatchmakingQueue queue in usageData.Queues)
                    {
                        queue.Remove(player);
                    }

                    usageData.SetPlaying();
                }
            }

            if (groups != null)
            {
                foreach (IPlayerGroup group in groups)
                {
                    if (!_groupUsageDictionary.TryGetValue(group, out UsageData usageData))
                        continue;

                    foreach (IMatchmakingQueue queue in usageData.Queues)
                    {
                        queue.Remove(group);
                    }

                    usageData.SetPlaying();
                }
            }
        }

        void IMatchmakingQueues.UnsetPlaying(List<PlayerOrGroup> toUnset)
        {
            if (toUnset == null)
                return;

            foreach (PlayerOrGroup pog in toUnset)
            {
                if (pog.Player != null)
                {
                    UnsetPlaying(pog.Player, true);
                }
                else if (pog.Group != null)
                {
                    UnsetPlaying(pog.Group, true);
                }
            }
        }

        void IMatchmakingQueues.UnsetPlayingWithoutRequeue(Player player)
        {
            UnsetPlaying(player, false);
        }

        void IMatchmakingQueues.UnsetPlayingWithoutRequeue(IPlayerGroup group)
        {
            UnsetPlaying(group, false);
        }

        ObjectPool<List<PlayerOrGroup>> IMatchmakingQueues.PlayerOrGroupListPool => _playerOrGroupListPool;

        private void UnsetPlaying(Player player, bool allowRequeue)
        {
            if (player == null)
                return;

            if (!player.TryGetExtraData(_pdKey, out UsageData usageData))
                return;

            usageData.UnsetPlaying();

            if (usageData.AutoQueues.Count > 0)
            {
                if (allowRequeue && usageData.AutoRequeue)
                {
                    // TODO: Set timer to requeue.
                    List<IMatchmakingQueue> addedQueues = _iMatchmakingQueueListPool.Get();
                    try
                    {
                        foreach (IMatchmakingQueue queue in usageData.AutoQueues)
                        {
                            if (Enqueue(player, null, usageData, queue))
                                addedQueues.Add(queue);
                            else
                                _logManager.LogP(LogLevel.Drivel, nameof(MatchmakingQueues), player, $"Failed to enqueue to: {queue.Name}.");
                        }

                        usageData.ClearAutoQueues();

                        NotifyQueuedAndInvokeChangeCallbacks(player, null, addedQueues);
                    }
                    finally
                    {
                        _iMatchmakingQueueListPool.Return(addedQueues);
                    }
                }
                else
                {
                    usageData.ClearAutoQueues();
                }
            }
        }

        private void UnsetPlaying(IPlayerGroup group, bool allowRequeue)
        {
            if (group == null)
                return;

            if (!_groupUsageDictionary.TryGetValue(group, out UsageData usageData))
                return;

            usageData.UnsetPlaying();

            if (usageData.AutoQueues.Count > 0)
            {
                if (allowRequeue && usageData.AutoRequeue)
                {
                    // TODO: Set timer to requeue.
                    List<IMatchmakingQueue> addedQueues = _iMatchmakingQueueListPool.Get();
                    try
                    {
                        foreach (IMatchmakingQueue queue in usageData.AutoQueues)
                        {
                            if (Enqueue(group.Leader, group, usageData, queue))
                                addedQueues.Add(queue);
                        }

                        usageData.ClearAutoQueues();

                        NotifyQueuedAndInvokeChangeCallbacks(null, group, addedQueues);
                    }
                    finally
                    {
                        _iMatchmakingQueueListPool.Return(addedQueues);
                    }
                }
                else
                {
                    usageData.ClearAutoQueues();
                }
            }
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
                // Remove the player from all queues.
                // Note: If the player was in a group that was queued, the group will remove the player and fire the PlayerGroupMemberRemovedCallback.
                if (!player.TryGetExtraData(_pdKey, out UsageData usageData))
                    return;

                if (usageData.Queues.Count > 0)
                {
                    // The player is searching for a match, stop the search.
                    RemoveFromAllQueues(player, null, usageData, false);
                }
            }
        }

        private void Callback_PlayerGroupMemberRemoved(IPlayerGroup group, Player player, PlayerGroupMemberRemovedReason reason)
        {
            if (!_groupUsageDictionary.TryGetValue(group, out UsageData usageData))
                return;

            if (usageData.Queues.Count > 0)
            {
                // The group is searching for a match, stop the search.
                RemoveFromAllQueues(null, group, usageData, true);
            }
        }

        private void Callback_PlayerGroupDisbanded(IPlayerGroup group)
        {
            if (_groupUsageDictionary.Remove(group, out UsageData usageData))
            {
                if (usageData.Queues.Count > 0)
                {
                    // The group is searching for a match, stop the search.
                    RemoveFromAllQueues(null, group, usageData, false);
                }

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
            Args = "<none> | <queue name>[, <queue name>[, ...]]] | -list | -listall | -auto",
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

            if (string.Equals(parameters, "-list", StringComparison.OrdinalIgnoreCase))
            {
                // Print usage details.
                switch (usageData.State)
                {
                    case QueueState.None:
                        _chat.SendMessage(player, $"{NextCommandName}: {(group == null ? "You are" : "Your group is")} not searching for a game yet.");
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

                            _chat.SendMessage(player, $"{NextCommandName}: {(group == null ? "You are" : "Your group is")} searching for a game on the following queues: {sb}");
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }
                        break;

                    case QueueState.Playing:
                        _chat.SendMessage(player, $"{NextCommandName}: {(group == null ? "You are" : "Your group is")} currently playing in match.");

                        if (usageData.AutoRequeue)
                        {
                            if (usageData.AutoQueues.Count > 0)
                            {
                                sb = _objectPoolManager.StringBuilderPool.Get();
                                try
                                {
                                    foreach (var queue in usageData.AutoQueues)
                                    {
                                        if (sb.Length > 0)
                                            sb.Append(", ");

                                        sb.Append(queue.Name);
                                    }

                                    _chat.SendMessage(player, $"{NextCommandName}: {(group == null ? "You are" : "Your group is")} set to automatically requeue to: {sb}");
                                }
                                finally
                                {
                                    _objectPoolManager.StringBuilderPool.Return(sb);
                                }
                            }
                            else
                            {
                                _chat.SendMessage(player, $"{NextCommandName}: {(group == null ? "You are" : "Your group is")} set to automatically requeue.");
                            }
                        }
                        break;

                    default:
                        break;
                }

                return;
            }
            else if (string.Equals(parameters, "-listall", StringComparison.OrdinalIgnoreCase))
            {
                // TOOD: include stats of how many players and groups are in each queeue
                foreach (var queue in _queues.Values)
                {
                    if (!string.IsNullOrWhiteSpace(queue.Description))
                        _chat.SendMessage(player, $"{NextCommandName}: {queue.Name} - {queue.Description}");
                    else
                        _chat.SendMessage(player, $"{NextCommandName}: {queue.Name}");
                }

                return;
            }
            else if (string.Equals(parameters, "-auto", StringComparison.OrdinalIgnoreCase))
            {
                _chat.SendMessage(player, $"{NextCommandName}: Automatic requeuing {(usageData.AutoRequeue ? "disabled" : "enabled")}.");
                usageData.AutoRequeue = !usageData.AutoRequeue;
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

            if (string.IsNullOrWhiteSpace(parameters))
            {
                // No queue name(s) were specified. Check if there is a default queue for the arena.
                var advisors = _broker.GetAdvisors<IMatchmakingQueueAdvisor>();
                foreach (var advisor in advisors)
                {
                    parameters = advisor.GetDefaultQueue(player.Arena);
                    if (!string.IsNullOrWhiteSpace(parameters))
                    {
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(parameters))
                {
                    _chat.SendMessage(player, $"{NextCommandName}: You must specify which queue(s) to search on.");
                    return;
                }
            }

            List<IMatchmakingQueue> addedQueues = _iMatchmakingQueueListPool.Get();

            try
            {
                ReadOnlySpan<char> remaining = parameters;
                ReadOnlySpan<char> token;
                while ((token = remaining.GetToken(", ", out remaining)).Length > 0)
                {
                    string queueName = token.ToString(); // FIXME: string allocation needed to search dictionary

                    IMatchmakingQueue queue = AddToQueue(player, group, usageData, queueName);
                    if (queue != null)
                    {
                        addedQueues.Add(queue);
                    }
                }

                NotifyQueuedAndInvokeChangeCallbacks(player, group, addedQueues);
            }
            finally
            {
                _iMatchmakingQueueListPool.Return(addedQueues);
            }

            IMatchmakingQueue AddToQueue(Player player, IPlayerGroup group, UsageData usageData, string queueName)
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
                    return null;
                }

                if (!Enqueue(player, group, usageData, queue))
                    return null;

                return queue;
            }
        }

        private bool Enqueue(Player player, IPlayerGroup group, UsageData usageData, IMatchmakingQueue queue)
        {
            if (group != null)
            {
                // group search
                if (!queue.Options.AllowGroups)
                {
                    _chat.SendMessage(player, $"{NextCommandName}: Queue '{queue.Name}' does not allow premade groups.");
                    return false;
                }

                if (group.Members.Count < queue.Options.MinGroupSize
                    || group.Members.Count > queue.Options.MaxGroupSize)
                {
                    if (queue.Options.MinGroupSize == queue.Options.MaxGroupSize)
                        _chat.SendMessage(player, $"{NextCommandName}: Queue '{queue.Name}' allows groups with exactly {queue.Options.MinGroupSize} players, but your group has {group.Members.Count} players.");
                    else
                        _chat.SendMessage(player, $"{NextCommandName}: Queue '{queue.Name}' allows groups sized from {queue.Options.MinGroupSize} to {queue.Options.MaxGroupSize} players, but your group has {group.Members.Count} players.");

                    return false;
                }

                if (!usageData.AddQueue(queue))
                {
                    _chat.SendMessage(player, $"{NextCommandName}: Already searching for a game on queue '{queue.Name}'.");
                    return false;
                }
                else if (!queue.Add(group))
                {
                    usageData.RemoveQueue(queue);
                    _chat.SendMessage(player, $"{NextCommandName}: Error adding to the '{queue.Name}' queue.");
                    return false;
                }

                return true;
            }
            else
            {
                // solo search
                if (!queue.Options.AllowSolo)
                {
                    _chat.SendMessage(player, $"{NextCommandName}: Queue '{queue.Name}' does not allow solo play. Create or join a group first.");
                    return false;
                }

                if (!usageData.AddQueue(queue))
                {
                    _chat.SendMessage(player, $"{NextCommandName}: Already searching for a game on queue '{queue.Name}'.");
                    return false;
                }
                else if (!queue.Add(player))
                {
                    usageData.RemoveQueue(queue);
                    _chat.SendMessage(player, $"{NextCommandName}: Error adding to the '{queue.Name}' queue.");
                    return false;
                }

                return true;
            }
        }

        private void NotifyQueuedAndInvokeChangeCallbacks(Player player, IPlayerGroup group, List<IMatchmakingQueue> addedQueues)
        {
            if (addedQueues == null || addedQueues.Count <= 0)
                return;

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
            try
            {
                foreach (IMatchmakingQueue queue in addedQueues)
                {
                    if (sb.Length > 0)
                        sb.Append(", ");

                    sb.Append(queue.Name);
                }

                if (group != null)
                {
                    // Notify the group members that a search has started.
                    HashSet<Player> members = _objectPoolManager.PlayerSetPool.Get();
                    try
                    {
                        members.UnionWith(group.Members);

                        _chat.SendSetMessage(members, $"{NextCommandName}: Started searching for a game on queue{((addedQueues.Count == 1) ? "" : "s")}: {sb}.");
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(members);
                    }
                }
                else if (player != null)
                {
                    // Notify the player that a search has started.
                    _chat.SendMessage(player, $"{NextCommandName}: Started searching for a game on queue{((addedQueues.Count == 1) ? "" : "s")}: {sb}.");
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }

            // Fire the callbacks in the order that the queues were added. This will cause matchmaking module to attempt matches on the queues in that order.
            // Note: Firing the callbacks is purposely done after adding to all requested queues so that in case there is a match, we can keep track of any queues that allow automatic requeuing.
            // Also, we want the above notifications to be sent before any other matching notifications.
            if (group != null)
            {
                foreach (IMatchmakingQueue queue in addedQueues)
                {
                    MatchmakingQueueChangedCallback.Fire(_broker, queue, QueueAction.Add, QueueItemType.Group);
                }
            }
            else
            {
                foreach (IMatchmakingQueue queue in addedQueues)
                {
                    MatchmakingQueueChangedCallback.Fire(_broker, queue, QueueAction.Add, QueueItemType.Player);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<none> | [<queue name>[, <queue name>[, ...]]]",
            Description = "Cancels a matchmaking search.\n" +
            "Use the command without specifying a <queue name> to remove from all matchmaking queues.")]
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
                    _chat.SendMessage(player, $"{CancelCommandName}: Only the group leader can cancel a search.");
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
                if (usageData.State == QueueState.Playing && usageData.AutoRequeue)
                {
                    usageData.AutoRequeue = false;
                    _chat.SendMessage(player, $"{CancelCommandName}: Automatic requeuing disabled.");
                    return;
                }

                if (usageData.Queues.Count == 0)
                {
                    _chat.SendMessage(player, $"{CancelCommandName}: There are no active searches to cancel.");
                    return;
                }

                RemoveFromAllQueues(player, group, usageData, true);

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
                        _chat.SendMessage(player, $"{CancelCommandName}: Queue '{queueName}' not found.");
                        continue;
                    }

                    if (!usageData.RemoveQueue(queue))
                    {
                        _chat.SendMessage(player, $"{CancelCommandName}: There is no active search to cancel on queue '{queue.Name}'.");
                        return;
                    }
                    else
                    {
                        if (group != null)
                            queue.Remove(group);
                        else
                            queue.Remove(player);
                    }

                    // Notify
                    if (group != null)
                    {
                        HashSet<Player> members = _objectPoolManager.PlayerSetPool.Get();
                        try
                        {
                            members.UnionWith(group.Members);
                            _chat.SendSetMessage(members, $"{CancelCommandName}: Search stopped on queue: {queue.Name}.");
                        }
                        finally
                        {
                            _objectPoolManager.PlayerSetPool.Return(members);
                        }
                    }
                    else
                    {
                        _chat.SendMessage(player, $"{CancelCommandName}: Search stopped on queue: {queue.Name}.");
                    }

                    MatchmakingQueueChangedCallback.Fire(_broker, queue, QueueAction.Remove, group != null ? QueueItemType.Group : QueueItemType.Player);
                }
            }
        }

        #endregion

        private void RemoveFromAllQueues(Player player, IPlayerGroup group, UsageData usageData, bool notify)
        {
            if (player == null && group == null)
                return; // must have a player, a group, or both

            if (usageData == null)
                return;

            if (usageData.Queues.Count == 0)
                return; // not searching

            List<IMatchmakingQueue> removedFrom = _iMatchmakingQueueListPool.Get();
            try
            {
                // Do the actual removal.
                foreach (var queue in usageData.Queues)
                {
                    bool removed = false;

                    if (group != null)
                        removed = queue.Remove(group);
                    else if (player != null)
                        removed = queue.Remove(player);

                    if (removed)
                    {
                        removedFrom.Add(queue);
                    }
                }

                usageData.RemoveAllQueues();

                // Notify the player(s).
                if (notify)
                {
                    if (group != null)
                    {
                        HashSet<Player> members = _objectPoolManager.PlayerSetPool.Get();
                        try
                        {
                            foreach (Player member in group.Members)
                                members.Add(member);

                            _chat.SendSetMessage(members, $"{CancelCommandName}: Search stopped.");
                        }
                        finally
                        {
                            _objectPoolManager.PlayerSetPool.Return(members);
                        }
                    }
                    else
                    {
                        _chat.SendMessage(player, $"{CancelCommandName}: Search stopped.");
                    }
                }

                // Fire the callback.
                foreach (var queue in removedFrom)
                {
                    MatchmakingQueueChangedCallback.Fire(_broker, queue, QueueAction.Remove, group != null ? QueueItemType.Group : QueueItemType.Player);
                }
            }
            finally
            {
                _iMatchmakingQueueListPool.Return(removedFrom);
            }
        }

        private enum QueueState
        {
            None,

            /// <summary>
            /// Searching for a match.
            /// </summary>
            Queued,

            /// <summary>
            /// Playing in a match. No searching allowed on queues.
            /// </summary>
            Playing,
        }

        private class UsageData
        {
            public QueueState State { get; private set; }
            public bool AutoRequeue = false;
            public readonly HashSet<IMatchmakingQueue> Queues = new();
            public readonly HashSet<IMatchmakingQueue> AutoQueues = new();

            public bool AddQueue(IMatchmakingQueue queue)
            {
                if (!Queues.Add(queue))
                    return false;

                if (State == QueueState.None)
                    State = QueueState.Queued;

                return true;
            }

            public bool RemoveQueue(IMatchmakingQueue queue)
            {
                if (!Queues.Remove(queue))
                    return false;

                if (State == QueueState.Queued && Queues.Count == 0)
                    State = QueueState.None;

                return true;
            }

            public bool RemoveAllQueues()
            {
                if (Queues.Count == 0)
                    return false;

                Queues.Clear();

                if (State == QueueState.Queued)
                    State = QueueState.None;

                return true;
            }

            public void SetPlaying()
            {
                State = QueueState.Playing;

                AutoQueues.Clear();

                foreach (var queue in Queues)
                {
                    if (queue.Options.AllowAutoRequeue)
                        AutoQueues.Add(queue);
                }

                Queues.Clear();
            }

            public void UnsetPlaying()
            {
                if (State == QueueState.Playing)
                    State = QueueState.None;
            }

            public void ClearAutoQueues()
            {
                AutoQueues.Clear();
            }

            public void Reset()
            {
                State = QueueState.None;
                Queues.Clear();
                AutoQueues.Clear();
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

        private class IMatchmakingQueueListPooledObjectPolicy : IPooledObjectPolicy<List<IMatchmakingQueue>>
        {
            public List<IMatchmakingQueue> Create()
            {
                return new List<IMatchmakingQueue>();
            }

            public bool Return(List<IMatchmakingQueue> obj)
            {
                if (obj == null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        private class PlayerOrGroupListPooledObjectPolicy : IPooledObjectPolicy<List<PlayerOrGroup>>
        {
            public List<PlayerOrGroup> Create()
            {
                return new List<PlayerOrGroup>();
            }

            public bool Return(List<PlayerOrGroup> obj)
            {
                if (obj == null)
                    return false;

                obj.Clear();
                return true;
            }
        }
    }
}

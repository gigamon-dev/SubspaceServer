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
    [ModuleInfo("Manages matchmaking queues.")]
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
        private readonly HashSet<string> _playersPlaying = new(256, StringComparer.OrdinalIgnoreCase); // player names (can't use Player objects since it needs to exist even if the player disconnects)

        private readonly ObjectPool<UsageData> _usageDataPool = new NonTransientObjectPool<UsageData>(new UsageDataPooledObjectPolicy()); // only for groups TODO: add a way to use the same pool as per-player data
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

            _pdKey = _playerData.AllocatePlayerData(new UsageDataPooledObjectPolicy());

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

            _playerData.FreePlayerData(ref _pdKey);

            if (_help is not null)
            {
                broker.ReleaseInterface(ref _help);
            }

            return true;
        }

        #endregion

        #region IMatchmakingQueues

        bool IMatchmakingQueues.RegisterQueue(IMatchmakingQueue queue)
        {
            if (queue is null)
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
            if (queue is null)
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

        void IMatchmakingQueues.SetPlaying(HashSet<Player> players)
        {
            if (players is null)
                return;

            foreach (Player player in players)
            {
                SetPlaying(player, false);
            }
        }

        void IMatchmakingQueues.SetPlayingAsSub(Player player)
        {
            SetPlaying(player, true);
        }

        private void SetPlaying(Player player, bool isSub)
        {
            if (player is null)
                return;

            // Individual
            if (player.TryGetExtraData(_pdKey, out UsageData usageData))
            {
                foreach (QueuedInfo queuedInfo in usageData.Queued)
                {
                    queuedInfo.Queue.Remove(player);
                }

                usageData.SetPlaying(isSub);

                _playersPlaying.Add(player.Name);
            }

            // Group
            IPlayerGroup group = _playerGroups.GetGroup(player);
            if (group is not null && _groupUsageDictionary.TryGetValue(group, out usageData))
            {
                foreach (QueuedInfo queuedInfo in usageData.Queued)
                {
                    queuedInfo.Queue.Remove(group);
                }

                usageData.SetPlaying(isSub);
            }
        }

        void IMatchmakingQueues.UnsetPlaying<T>(T players, bool allowRequeue)
        {
            foreach (Player player in players)
            {
                UnsetPlaying(player, allowRequeue);
            }
        }

        void IMatchmakingQueues.UnsetPlaying(Player player, bool allowRequeue)
        {
            UnsetPlaying(player, allowRequeue);
        }

        void IMatchmakingQueues.UnsetPlaying(string playerName, bool allowAutoRequeue)
        {
            Player player = _playerData.FindPlayer(playerName);
            if (player is not null)
            {
               UnsetPlaying(player, allowAutoRequeue);
            }
            else
            {
                _playersPlaying.Remove(playerName);
            }
        }

        private void UnsetPlaying(Player player, bool allowRequeue)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out UsageData usageData))
                return;

            if (usageData.UnsetPlaying(out bool wasSub))
            {
                _playersPlaying.Remove(player.Name);

                if (usageData.PreviousQueued.Count > 0)
                {
                    if (allowRequeue && (wasSub || usageData.AutoRequeue))
                    {
                        // TODO: Maybe instead of doing this immediately, set a timer to do it. That way there will be a delay after match completion?
                        List<IMatchmakingQueue> addedQueues = _iMatchmakingQueueListPool.Get();
                        try
                        {
                            foreach ((IMatchmakingQueue queue, DateTime timestamp) in usageData.PreviousQueued)
                            {
                                if (!wasSub && !queue.Options.AllowAutoRequeue)
                                    continue;

                                if (Enqueue(player, null, usageData, queue, wasSub ? timestamp : DateTime.UtcNow))
                                    addedQueues.Add(queue);
                            }

                            usageData.ClearPreviousQueued();

                            NotifyQueuedAndInvokeChangeCallbacks(player, null, addedQueues);
                        }
                        finally
                        {
                            _iMatchmakingQueueListPool.Return(addedQueues);
                        }
                    }
                    else
                    {
                        usageData.ClearPreviousQueued();
                    }
                }
            }

            IPlayerGroup group = _playerGroups.GetGroup(player);
            if (group is not null)
            {
                UnsetPlaying(group, allowRequeue);
            }
        }

        private void UnsetPlaying(IPlayerGroup group, bool allowRequeue)
        {
            if (group is null)
                return;

            foreach (Player member in group.Members)
            {
                if (!member.TryGetExtraData(_pdKey, out UsageData memberUsageData))
                    continue;

                if (memberUsageData.State == QueueState.Playing)
                    return; // consider the group to still be playing if at least one member is playing
            }

            if (!_groupUsageDictionary.TryGetValue(group, out UsageData usageData))
                return;

            if (usageData.UnsetPlaying(out bool wasSub) && usageData.PreviousQueued.Count > 0)
            {
                if (allowRequeue && usageData.AutoRequeue)
                {
                    // TODO: Maybe instead of doing this immediately, set a timer to do it. That way there will be a delay after match completion?
                    List<IMatchmakingQueue> addedQueues = _iMatchmakingQueueListPool.Get();
                    try
                    {
                        foreach ((IMatchmakingQueue queue, DateTime timestamp) in usageData.PreviousQueued)
                        {
                            if (!wasSub && !queue.Options.AllowAutoRequeue)
                                continue;

                            if (Enqueue(group.Leader, group, usageData, queue, wasSub ? timestamp : DateTime.UtcNow))
                                addedQueues.Add(queue);
                        }

                        usageData.ClearPreviousQueued();

                        NotifyQueuedAndInvokeChangeCallbacks(null, group, addedQueues);
                    }
                    finally
                    {
                        _iMatchmakingQueueListPool.Return(addedQueues);
                    }
                }
                else
                {
                    usageData.ClearPreviousQueued();
                }
            }
        }

        #endregion

        #region IPlayerGroupAdvisor

        bool IPlayerGroupAdvisor.AllowSendInvite(IPlayerGroup group, StringBuilder message)
        {
            if (group is null || !_groupUsageDictionary.TryGetValue(group, out UsageData usageData))
                return true;

            if (usageData.State == QueueState.Queued)
            {
                message?.Append("Cannot invite while searching for a match. To invite, stop the search first.");
                return false;
            }
            else if (usageData.State == QueueState.Playing)
            {
                message?.Append("Cannot invite while playing in a match. To invite, complete the current match.");
                return false;
            }
            else
            {
                return true;
            }
        }

        bool IPlayerGroupAdvisor.AllowAcceptInvite(Player player, StringBuilder message)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out UsageData usageData))
                return true;

            if (usageData.State == QueueState.Queued)
            {
                message?.Append("Cannot accept an invite while searching for a match. To accept, stop the search first.");
                return false;
            }
            else if (usageData.State == QueueState.Playing)
            {
                message?.Append("Cannot accept an invite while while playing in a match. To accept, complete the current match.");
                return false;
            }
            else
            {
                return true;
            }
        }

        #endregion

        #region Callbacks

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.Connect)
            {
                if (_playersPlaying.Contains(player.Name))
                {
                    if (!player.TryGetExtraData(_pdKey, out UsageData usageData))
                        return;

                    // The player disconnected while playing in match, but has now reconnected.
                    // For players that disconnected, we do not keep track of the previous queues. So, it doesn't matter if they were a sub.
                    usageData.SetPlaying(false);

                    // TODO: send a message to the player that they're still in the match

                    // TODO: automatically move the player to the proper arena
                    // Can't use IArenaManager.SendToArena() here because this event happens before the player is in the proper state to allow that.
                    // But, a hacky way to do it is to overwrite:
                    // player.ConnectAs = 
                    // which will tell the ArenaPlaceMultiPub module to send them to the proper arena when the client sends the Go packet.
                    // However, this module doesn't know the arena name. The match module needs to do it.
                }
            }
            else if (action == PlayerAction.Disconnect)
            {
                // Remove the player from all queues.
                // Note: If the player was in a group that was queued, the group will remove the player and fire the PlayerGroupMemberRemovedCallback.
                if (!player.TryGetExtraData(_pdKey, out UsageData usageData))
                    return;

                if (usageData.Queued.Count > 0)
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

            if (usageData.State == QueueState.Queued)
            {
                // The group is searching for a match, stop the search.
                RemoveFromAllQueues(null, group, usageData, true);
            }
            else if (usageData.State == QueueState.Playing)
            {
                // The group is playing in a match, remove automatic requeuing.
                usageData.ClearPreviousQueued();
            }
        }

        private void Callback_PlayerGroupDisbanded(IPlayerGroup group)
        {
            if (_groupUsageDictionary.Remove(group, out UsageData usageData))
            {
                if (usageData.Queued.Count > 0)
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
            Description = """
                Starts a matchmaking search.
                An arena may be configured with a default search queue, in which case, specifying a <queue name> is not necessary.
                """)]
        private void Command_next(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player.Status != PlayerState.Playing || player.Arena is null)
                return;

            // Check if the player is in a group.
            IPlayerGroup group = _playerGroups.GetGroup(player);

            // Get the usage data.
            UsageData usageData;
            if (group is not null)
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

            if (parameters.Equals("-list", StringComparison.OrdinalIgnoreCase))
            {
                // Print usage details.
                StringBuilder sb;
                switch (usageData.State)
                {
                    case QueueState.None:
                        _chat.SendMessage(player, $"{NextCommandName}: {(group is null ? "You are" : "Your group is")} not searching for a game yet.");
                        break;

                    case QueueState.Queued:
                        sb = _objectPoolManager.StringBuilderPool.Get();
                        try
                        {
                            foreach (var queuedInfo in usageData.Queued)
                            {
                                if (sb.Length > 0)
                                    sb.Append(", ");

                                sb.Append(queuedInfo.Queue.Name);
                            }

                            _chat.SendMessage(player, $"{NextCommandName}: {(group is null ? "You are" : "Your group is")} searching for a game on the following queues: {sb}");
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }
                        break;

                    case QueueState.Playing:
                        sb = _objectPoolManager.StringBuilderPool.Get();
                        try
                        {
                            foreach (var advisor in player.Arena.GetAdvisors<IMatchmakingQueueAdvisor>())
                            {
                                if (advisor.TryGetCurrentMatchInfo(player.Name, sb))
                                {
                                    break;
                                }
                            }

                            if (sb.Length > 0)
                            {
                                _chat.SendMessage(player, $"{NextCommandName}: {(group is null ? "You are" : "Your group is")} currently playing in a match: {sb}");
                            }
                            else
                            {
                                _chat.SendMessage(player, $"{NextCommandName}: {(group is null ? "You are" : "Your group is")} currently playing in a match.");
                            }
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }

                        if (usageData.AutoRequeue)
                        {
                            if (usageData.PreviousQueued.Count > 0)
                            {
                                sb = _objectPoolManager.StringBuilderPool.Get();
                                try
                                {
                                    foreach (var queuedInfo in usageData.PreviousQueued)
                                    {
                                        if (sb.Length > 0)
                                            sb.Append(", ");

                                        sb.Append(queuedInfo.Queue.Name);
                                    }

                                    _chat.SendMessage(player, $"{NextCommandName}: {(group is null ? "You are" : "Your group is")} set to automatically requeue to: {sb}");
                                }
                                finally
                                {
                                    _objectPoolManager.StringBuilderPool.Return(sb);
                                }
                            }
                            else
                            {
                                _chat.SendMessage(player, $"{NextCommandName}: {(group is null ? "You are" : "Your group is")} set to automatically requeue.");
                            }
                        }
                        break;

                    default:
                        break;
                }

                return;
            }
            else if (parameters.Equals("-listall", StringComparison.OrdinalIgnoreCase))
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
            else if (parameters.Equals("-auto", StringComparison.OrdinalIgnoreCase))
            {
                _chat.SendMessage(player, $"{NextCommandName}: Automatic requeuing {(usageData.AutoRequeue ? "disabled" : "enabled")}.");
                usageData.AutoRequeue = !usageData.AutoRequeue;
                return;
            }

            // The command is to start a search.

            if (group is not null)
            {
                if (player != group.Leader)
                {
                    _chat.SendMessage(player, $"{NextCommandName}: Only the group leader can start a search.");
                    return;
                }

                if (group.PendingMembers.Count > 0)
                {
                    _chat.SendMessage(player, $"{NextCommandName}: Can't start a search while there are pending invites to the group.");
                    return;
                }

                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                try
                {
                    int playingCount = 0;
                    foreach (Player member in group.Members)
                    {
                        if (member.TryGetExtraData(_pdKey, out UsageData memberUsage)
                            && memberUsage.State == QueueState.Playing)
                        {
                            if (sb.Length > 0)
                                sb.Append(", ");

                            sb.Append(member.Name);

                            playingCount++;
                        }
                    }

                    if (playingCount == 1)
                    {
                        _chat.SendMessage(player, $"{NextCommandName}: Can't start a search because {sb} is currently playing.");
                        return;
                    }
                    else if (playingCount > 1)
                    {
                        _chat.SendMessage(player, $"{NextCommandName}: Can't start a search because the following members are currently playing: {sb}");
                        return;
                    }
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }

            if (usageData.State == QueueState.Playing)
            {
                _chat.SendMessage(player, $"{NextCommandName}: Can't start a search while playing in a match.");
                return;
            }

            if (parameters.IsWhiteSpace())
            {
                // No queue name(s) were specified. Check if there is a default queue for the arena.
                var advisors = _broker.GetAdvisors<IMatchmakingQueueAdvisor>();
                foreach (var advisor in advisors)
                {
                    parameters = advisor.GetDefaultQueue(player.Arena);
                    if (!parameters.IsWhiteSpace())
                    {
                        break;
                    }
                }

                if (parameters.IsWhiteSpace())
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
                    if (queue is not null)
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
                if (!_queues.TryGetValue(queueName, out IMatchmakingQueue queue))
                {
                    // Did not find a queue with the name provided.
                    // Check if the name provided is an alias.
                    Arena arena = player.Arena;
                    var advisors = arena.GetAdvisors<IMatchmakingQueueAdvisor>();
                    foreach (IMatchmakingQueueAdvisor advisor in advisors)
                    {
                        string alias = advisor.GetQueueNameByAlias(arena, queueName);
                        if (alias is not null && _queues.TryGetValue(alias, out queue))
                        {
                            queueName = alias;
                            break;
                        }
                    }
                }

                if (queue is null)
                {
                    _chat.SendMessage(player, $"{NextCommandName}: Queue '{queueName}' not found.");
                    return null;
                }

                if (!Enqueue(player, group, usageData, queue, DateTime.UtcNow))
                    return null;

                return queue;
            }
        }

        private bool Enqueue(Player player, IPlayerGroup group, UsageData usageData, IMatchmakingQueue queue, DateTime timestamp)
        {
            if (group is not null)
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

                if (!usageData.AddQueue(queue, timestamp))
                {
                    _chat.SendMessage(player, $"{NextCommandName}: Already searching for a game on queue '{queue.Name}'.");
                    return false;
                }
                else if (!queue.Add(group, timestamp))
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

                if (!usageData.AddQueue(queue, timestamp))
                {
                    _chat.SendMessage(player, $"{NextCommandName}: Already searching for a game on queue '{queue.Name}'.");
                    return false;
                }
                else if (!queue.Add(player, timestamp))
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
            if (addedQueues is null || addedQueues.Count <= 0)
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

                if (group is not null)
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
                else if (player is not null)
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
            if (group is not null)
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
            Description = """
                Cancels a matchmaking search.
                Use the command without specifying a <queue name> to remove from all matchmaking queues.
                """)]
        private void Command_cancel(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            // Check if the player is in a group.
            IPlayerGroup group = _playerGroups.GetGroup(player);

            // Get the usage data.
            UsageData usageData;
            if (group is not null)
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

            if (parameters.IsWhiteSpace())
            {
                if (usageData.State == QueueState.Playing && usageData.AutoRequeue)
                {
                    usageData.AutoRequeue = false;
                    _chat.SendMessage(player, $"{CancelCommandName}: Automatic requeuing disabled.");
                    return;
                }

                if (usageData.Queued.Count == 0)
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
                        if (arena is not null)
                        {
                            var advisors = arena.GetAdvisors<IMatchmakingQueueAdvisor>();
                            foreach (IMatchmakingQueueAdvisor advisor in advisors)
                            {
                                string alias = advisor.GetQueueNameByAlias(arena, queueName);
                                if (alias is not null && _queues.TryGetValue(alias, out queue))
                                {
                                    queueName = alias;
                                    break;
                                }
                            }
                        }
                    }

                    if (queue is null)
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
                        if (group is not null)
                            queue.Remove(group);
                        else
                            queue.Remove(player);
                    }

                    // Notify
                    if (group is not null)
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

                    MatchmakingQueueChangedCallback.Fire(_broker, queue, QueueAction.Remove, group is not null ? QueueItemType.Group : QueueItemType.Player);
                }
            }
        }

        #endregion

        private void RemoveFromAllQueues(Player player, IPlayerGroup group, UsageData usageData, bool notify)
        {
            if (player is null && group is null)
                return; // must have a player, a group, or both

            if (usageData is null)
                return;

            if (usageData.Queued.Count == 0)
                return; // not searching

            List<IMatchmakingQueue> removedFrom = _iMatchmakingQueueListPool.Get();
            try
            {
                // Do the actual removal.
                foreach (var queuedInfo in usageData.Queued)
                {
                    bool removed = false;

                    if (group is not null)
                        removed = queuedInfo.Queue.Remove(group);
                    else if (player is not null)
                        removed = queuedInfo.Queue.Remove(player);

                    if (removed)
                    {
                        removedFrom.Add(queuedInfo.Queue);
                    }
                }

                usageData.RemoveAllQueues();

                // Notify the player(s).
                if (notify)
                {
                    if (group is not null)
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
                    MatchmakingQueueChangedCallback.Fire(_broker, queue, QueueAction.Remove, group is not null ? QueueItemType.Group : QueueItemType.Player);
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

        private readonly record struct QueuedInfo(IMatchmakingQueue Queue, DateTime Timestamp);

        private class UsageData
        {
            public QueueState State { get; private set; }
            public bool AutoRequeue = false;
            public bool IsPlayingAsSub = false;

            // TODO: Maybe better to use LinkedList<QueuedInfo> but then have to deal with pooling of LinkedListNode<QueuedInfo> objects.

            public readonly List<QueuedInfo> Queued = new();
            public readonly List<QueuedInfo> PreviousQueued = new();

            public bool AddQueue(IMatchmakingQueue queue, DateTime timestamp)
            {
                // Check if we're already searching the queue.
                foreach (QueuedInfo queuedInfo in Queued)
                {
                    if (queuedInfo.Queue == queue)
                        return false;
                }

                // Add it, keeping the list sorted by timestamp.
                int i;
                for (i = Queued.Count - 1; i >= 0; i--)
                {
                    QueuedInfo queuedInfo = Queued[i];
                    if (timestamp >= queuedInfo.Timestamp)
                    {
                        break;
                    }
                }
                Queued.Insert(i + 1, new QueuedInfo(queue, timestamp));

                if (State == QueueState.None)
                    State = QueueState.Queued;

                return true;
            }

            public bool RemoveQueue(IMatchmakingQueue queue)
            {
                if (!TryRemove(queue))
                    return false;

                if (State == QueueState.Queued && Queued.Count == 0)
                    State = QueueState.None;

                return true;

                bool TryRemove(IMatchmakingQueue queue)
                {
                    for (int i = 0; i < Queued.Count; i++)
                    {
                        if (Queued[i].Queue == queue)
                        {
                            Queued.RemoveAt(i);
                            return true;
                        }
                    }

                    return false;
                }
            }

            public bool RemoveAllQueues()
            {
                if (Queued.Count == 0)
                    return false;

                Queued.Clear();

                if (State == QueueState.Queued)
                    State = QueueState.None;

                return true;
            }

            public void SetPlaying(bool isSub)
            {
                State = QueueState.Playing;
                IsPlayingAsSub = isSub;

                PreviousQueued.Clear();

                foreach (QueuedInfo queuedInfo in Queued)
                {
                    PreviousQueued.Add(queuedInfo);
                }

                Queued.Clear();
            }

            public bool UnsetPlaying(out bool wasSub)
            {
                if (State == QueueState.Playing)
                {
                    State = QueueState.None;
                    wasSub = IsPlayingAsSub;
                    IsPlayingAsSub = false;
                    return true;
                }

                wasSub = false;
                return false;
            }

            public void ClearPreviousQueued()
            {
                PreviousQueued.Clear();
            }

            public void Reset()
            {
                State = QueueState.None;
                AutoRequeue = false;
                IsPlayingAsSub = false;
                Queued.Clear();
                PreviousQueued.Clear();
            }
        }

        private class UsageDataPooledObjectPolicy : IPooledObjectPolicy<UsageData>
        {
            public UsageData Create()
            {
                return new UsageData();
            }

            public bool Return(UsageData obj)
            {
                if (obj is null)
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
                if (obj is null)
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
                if (obj is null)
                    return false;

                obj.Clear();
                return true;
            }
        }
    }
}

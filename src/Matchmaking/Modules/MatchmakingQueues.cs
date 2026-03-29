using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Advisors;
using SS.Matchmaking.Callbacks;
using SS.Matchmaking.Interfaces;
using SS.Utilities;
using SS.Utilities.ObjectPool;
using System.Buffers;
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
    public sealed class MatchmakingQueues : IAsyncModule, IMatchmakingQueues, IPlayerGroupAdvisor
    {
        // required dependencies
        private readonly IComponentBroker _broker;
        private readonly IChat _chat;
        private readonly ICapabilityManager _capabilityManager;
        private readonly ICommandManager _commandManager;
        private readonly ILogManager _logManager;
        private readonly IMainloop _mainloop;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;
        private readonly IPlayerGroups _playerGroups;

        // optional dependencies
        private IHelp? _help;
        private IPersist? _persist;
        private ILeagueAuthorization? _leagueAuthorization;

        private InterfaceRegistrationToken<IMatchmakingQueues>? _iMatchmakingQueuesToken;

        private PlayerDataKey<PlayerData> _pdKey;
        private PlayerDataKey<UsageData> _puKey;

        private DelegatePersistentData<Player>? _persistRegistration;

        private readonly Dictionary<string, IMatchmakingQueue> _queues = new(16, StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IMatchmakingQueue>.AlternateLookup<ReadOnlySpan<char>> _queuesLookup;
        private readonly Dictionary<IPlayerGroup, UsageData> _groupUsageDictionary = new(128);

        /// <summary>
        /// Names of players that are currently playing.
        /// </summary>
        /// <remarks>
        /// Can't use Player objects since it needs to exist even if the player disconnects.
        /// </remarks>
        private readonly HashSet<string> _playersPlaying = new(256, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Pending play holds on players that have disconnected.
        /// </summary>
        /// <remarks>
        /// Key: player name
        /// </remarks>
        private readonly Dictionary<string, TimeSpan> _pendingPlayHolds = new(StringComparer.OrdinalIgnoreCase); // TODO: better if this was in the database, it could potentially grow large

        /// <summary>
        /// Changes to matchmaking queues that need the <see cref="MatchmakingQueueChangedCallback"/> invoked.
        /// </summary>
        private readonly Queue<QueueChangeRecord> _changesQueue = new(64);

        // Cached delegate
        private readonly Action<object?> _mainloopWork_InvokeQueueChangeCallback;

        private readonly DefaultObjectPool<UsageData> _usageDataPool = new(new DefaultPooledObjectPolicy<UsageData>(), Constants.TargetPlayerCount * 2);
        private readonly DefaultObjectPool<List<IMatchmakingQueue>> _iMatchmakingQueueListPool = new(new ListPooledObjectPolicy<IMatchmakingQueue>() { InitialCapacity = 32 });
        private readonly DefaultObjectPool<HashSet<IPlayerGroup>> _playerGroupSetPool = new(new HashSetPooledObjectPolicy<IPlayerGroup>() { InitialCapacity = 64 });

        public MatchmakingQueues(
            IComponentBroker broker,
            ICapabilityManager capabilityManager,
            IChat chat,
            ICommandManager commandManager,
            ILogManager logManager,
            IMainloop mainloop,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IPlayerGroups playerGroups)
        {
            _queuesLookup = _queues.GetAlternateLookup<ReadOnlySpan<char>>();

            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _playerGroups = playerGroups ?? throw new ArgumentNullException(nameof(playerGroups));

            _mainloopWork_InvokeQueueChangeCallback = MainloopWork_InvokeQueueChangeCallback;
        }

        #region Module members

        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _help = broker.GetInterface<IHelp>();
            _persist = broker.GetInterface<IPersist>();
            _leagueAuthorization = broker.GetInterface<ILeagueAuthorization>();

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();
            _puKey = _playerData.AllocatePlayerData(_usageDataPool);

            if (_persist is not null)
            {
                _persistRegistration = new DelegatePersistentData<Player>(
                    (int)Persist.PersistKey.MatchmakingQueuesPlayerData, PersistInterval.Forever, PersistScope.Global, Persist_GetData, Persist_SetData, Persist_ClearData);
                await _persist.RegisterPersistentDataAsync(_persistRegistration);
            }

            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            PlayerGroupMemberAddedCallback.Register(broker, Callback_PlayerGroupMemberAdded);
            PlayerGroupMemberRemovedCallback.Register(broker, Callback_PlayerGroupMemberRemoved);
            PlayerGroupDisbandedCallback.Register(broker, Callback_PlayerGroupDisbanded);

            _commandManager.AddCommand(CommandNames.Next, Command_next);
            _commandManager.AddCommand(CommandNames.CancelNext, Command_cancelnext);
            _commandManager.AddCommand(CommandNames.CancelNextAlt, Command_cancelnext);
            _commandManager.AddCommand(CommandNames.NextHold, Command_nextHold);
            _commandManager.AddCommand(CommandNames.ManagePlayingState, Command_manageplayingstate);

            _iMatchmakingQueuesToken = broker.RegisterInterface<IMatchmakingQueues>(this);
            return true;
        }

        async Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            broker.UnregisterInterface(ref _iMatchmakingQueuesToken);

            _commandManager.RemoveCommand(CommandNames.Next, Command_next);
            _commandManager.RemoveCommand(CommandNames.CancelNext, Command_cancelnext);
            _commandManager.RemoveCommand(CommandNames.CancelNextAlt, Command_cancelnext);
            _commandManager.RemoveCommand(CommandNames.NextHold, Command_nextHold);
            _commandManager.RemoveCommand(CommandNames.ManagePlayingState, Command_manageplayingstate);

            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            PlayerGroupMemberAddedCallback.Unregister(broker, Callback_PlayerGroupMemberAdded);
            PlayerGroupMemberRemovedCallback.Unregister(broker, Callback_PlayerGroupMemberRemoved);
            PlayerGroupDisbandedCallback.Unregister(broker, Callback_PlayerGroupDisbanded);

            if (_persist is not null && _persistRegistration is not null)
                await _persist.UnregisterPersistentDataAsync(_persistRegistration);

            _playerData.FreePlayerData(ref _pdKey);
            _playerData.FreePlayerData(ref _puKey);

            if (_help is not null)
            {
                broker.ReleaseInterface(ref _help);
            }

            if (_persist is not null)
            {
                broker.ReleaseInterface(ref _persist);
            }

            if (_leagueAuthorization is not null)
            {
                broker.ReleaseInterface(ref _leagueAuthorization);
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

            if (queue.Options.PermitLeagueId is not null && _leagueAuthorization is null)
            {
                _logManager.LogM(LogLevel.Warn, nameof(MatchmakingQueues), $"Queue '{queue.Name}' has PermitLeagueId '{queue.Options.PermitLeagueId.Value}', but the required {nameof(ILeagueAuthorization)} service was not found.");
                return false;
            }

            try
            {
                return _queues.TryAdd(queue.Name, queue);
            }
            finally
            {
                if (queue.Options.PermitLeagueId is not null && _leagueAuthorization is not null)
                {
                    _leagueAuthorization.Register(queue.Options.PermitLeagueId.Value, LeagueRole.PracticePermit);
                    _leagueAuthorization.Register(queue.Options.PermitLeagueId.Value, LeagueRole.PermitManager);
                }
            }
        }

        bool IMatchmakingQueues.UnregisterQueue(IMatchmakingQueue queue)
        {
            if (queue is null)
                return false;

            if (!_queues.Remove(queue.Name, out IMatchmakingQueue? removedQueue))
                return false;

            if (queue != removedQueue)
            {
                _queues.Add(removedQueue.Name, removedQueue);
                return false;
            }

            if (queue.Options.PermitLeagueId is not null && _leagueAuthorization is not null)
            {
                _leagueAuthorization.Unregister(queue.Options.PermitLeagueId.Value, LeagueRole.PracticePermit);
                _leagueAuthorization.Unregister(queue.Options.PermitLeagueId.Value, LeagueRole.PermitManager);
            }

            //
            // Clear the queue
            //

            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
            HashSet<IPlayerGroup> groups = _playerGroupSetPool.Get();

            try
            {
                removedQueue.GetQueued(players, groups);

                foreach (Player player in players)
                {
                    removedQueue.Remove(player);

                    if (!player.TryGetExtraData(_puKey, out UsageData? usageData))
                        continue;

                    usageData.RemoveQueue(removedQueue);
                }

                foreach (IPlayerGroup group in groups)
                {
                    removedQueue.Remove(group);

                    if (!_groupUsageDictionary.TryGetValue(group, out UsageData? usageData))
                        continue;

                    usageData.RemoveQueue(removedQueue);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(players);
                _playerGroupSetPool.Return(groups);
            }

            return true;
        }

        IMatchmakingQueue? IMatchmakingQueues.GetQueue(ReadOnlySpan<char> name)
        {
            if (_queuesLookup.TryGetValue(name, out IMatchmakingQueue? queue))
                return queue;

            return null;
        }

        void IMatchmakingQueues.SetPlaying(Player player)
        {
            if (player is null)
                return;

            SetPlaying(player, false);
        }

        void IMatchmakingQueues.SetPlaying<T>(T players)
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
            if (player.TryGetExtraData(_puKey, out UsageData? usageData))
            {
                foreach (QueuedInfo queuedInfo in usageData.Queued)
                {
                    queuedInfo.Queue.Remove(player);
                }

                usageData.SetPlaying(isSub);

                _playersPlaying.Add(player.Name!);
            }

            // Group
            IPlayerGroup? group = _playerGroups.GetGroup(player);
            if (group is not null && _groupUsageDictionary.TryGetValue(group, out usageData))
            {
                foreach (QueuedInfo queuedInfo in usageData.Queued)
                {
                    queuedInfo.Queue.Remove(group);
                }

                usageData.SetPlaying(isSub);
            }
        }

        void IMatchmakingQueues.UnsetPlayingDueToCancel(Player player)
        {
            UnsetPlaying(player, true, true);
        }

        void IMatchmakingQueues.UnsetPlayingDueToCancel<T>(T players)
        {
            // First, unset all players (and their groups) to restore their previous queue positions.
            // Then, fire the change events for the affected queues.
            UnsetPlaying(players, true, true);
        }

        void IMatchmakingQueues.UnsetPlaying(Player player, bool allowRequeue)
        {
            UnsetPlaying(player, allowRequeue, false);
        }

        void IMatchmakingQueues.UnsetPlaying<T>(T players, bool allowRequeue)
        {
            UnsetPlaying(players, allowRequeue, false);
        }

        void IMatchmakingQueues.UnsetPlayingByName(string playerName, bool allowAutoRequeue)
        {
            Player? player = _playerData.FindPlayer(playerName);
            if (player is not null)
            {
                UnsetPlaying(player, allowAutoRequeue, false);
            }
            else
            {
                _playersPlaying.Remove(playerName);
            }
        }

        void IMatchmakingQueues.UnsetPlayingByName<T>(T playerNames, bool allowAutoRequeue)
        {
            Player[] players = ArrayPool<Player>.Shared.Rent(playerNames.Count);

            try
            {
                int index = 0;

                foreach (string playerName in playerNames)
                {
                    Player? player = _playerData.FindPlayer(playerName);
                    if (player is not null)
                    {
                        players[index++] = player;
                    }
                    else
                    {
                        _playersPlaying.Remove(playerName);
                    }
                }

                UnsetPlaying(new ArraySegment<Player>(players, 0, index), allowAutoRequeue, false);
            }
            finally
            {
                ArrayPool<Player>.Shared.Return(players, true);
            }
        }

        void IMatchmakingQueues.UnsetPlayingAfterDelay(string playerName, TimeSpan delay)
        {
            if (!_playersPlaying.Remove(playerName))
                return;

            Player? player = _playerData.FindPlayer(playerName);
            if (player is not null)
            {
                SetPlayHold(player, delay);
            }
            else
            {
                // The player is not logged on.
                // However, we still want to remember that the player should have a play hold if they reconnect.
                if (!_pendingPlayHolds.TryGetValue(playerName, out TimeSpan duration)
                    || duration < delay)
                {
                    _pendingPlayHolds[playerName] = delay;
                }
            }
        }

        void IMatchmakingQueues.UnsetPlayingAfterDelay<T>(T playerNames, TimeSpan delay)
        {
            if (playerNames is null)
                return;

            foreach (string playerName in playerNames)
            {
                ((IMatchmakingQueues)this).UnsetPlayingAfterDelay(playerName, delay);
            }
        }

        private void UnsetPlaying<T>(T players, bool allowRequeue, bool isDueToCancel) where T : IReadOnlyCollection<Player>
        {
            if (players is null)
                return;

            List<IMatchmakingQueue> allAddedQueues = _iMatchmakingQueueListPool.Get();

            try
            {
                foreach (Player player in players)
                {
                    UnsetPlaying(player, allowRequeue, isDueToCancel, allAddedQueues);
                }

                ScheduleQueueChangedCallbacks(allAddedQueues, QueueAction.Add);
            }
            finally
            {
                _iMatchmakingQueueListPool.Return(allAddedQueues);
            }
        }

        private void UnsetPlaying(Player player, bool allowRequeue, bool isDueToCancel)
        {
            if (player is null)
                return;

            List<IMatchmakingQueue> allAddedQueues = _iMatchmakingQueueListPool.Get();

            try
            {
                UnsetPlaying(player, allowRequeue, isDueToCancel, allAddedQueues);
                ScheduleQueueChangedCallbacks(allAddedQueues, QueueAction.Add);
            }
            finally
            {
                _iMatchmakingQueueListPool.Return(allAddedQueues);
            }
        }

        private void UnsetPlaying(Player player, bool allowRequeue, bool isDueToCancel, List<IMatchmakingQueue> allAddedQueues)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_puKey, out UsageData? usageData))
                return;

            if (usageData.UnsetPlaying(out bool wasSub))
            {
                _playersPlaying.Remove(player.Name!);

                if (usageData.PreviousQueued.Count > 0)
                {
                    if (allowRequeue && (isDueToCancel || wasSub || usageData.AutoRequeue))
                    {
                        List<IMatchmakingQueue> addedQueues = _iMatchmakingQueueListPool.Get();
                        try
                        {
                            foreach ((IMatchmakingQueue queue, DateTime timestamp) in usageData.PreviousQueued)
                            {
                                if (!isDueToCancel && !wasSub && !queue.Options.AllowAutoRequeue)
                                    continue;

                                if (Enqueue(player, null, usageData, queue, isDueToCancel || wasSub ? timestamp : DateTime.UtcNow))
                                    addedQueues.Add(queue);
                            }

                            usageData.ClearPreviousQueued();

                            NotifyQueuedAndInvokeChangeCallbacks(player, null, addedQueues, false);

                            foreach (var queue in addedQueues)
                            {
                                if (allAddedQueues.Contains(queue))
                                    continue;

                                allAddedQueues.Add(queue);
                            }
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

            IPlayerGroup? group = _playerGroups.GetGroup(player);
            if (group is not null)
            {
                UnsetPlaying(group, allowRequeue, isDueToCancel, allAddedQueues);
            }
        }

        private void UnsetPlaying(IPlayerGroup group, bool allowRequeue, bool isDueToCancel, List<IMatchmakingQueue> allAddedQueues)
        {
            if (group is null)
                return;

            foreach (Player member in group.Members)
            {
                if (member.TryGetExtraData(_puKey, out UsageData? memberUsageData)
                    && memberUsageData.State == QueueState.Playing)
                {
                    // Consider the group to still be playing if at least one member is playing.
                    return;
                }
            }

            if (!_groupUsageDictionary.TryGetValue(group, out UsageData? usageData))
                return;

            if (usageData.UnsetPlaying(out bool wasSub) && usageData.PreviousQueued.Count > 0)
            {
                if (allowRequeue && (isDueToCancel || wasSub || usageData.AutoRequeue))
                {
                    List<IMatchmakingQueue> addedQueues = _iMatchmakingQueueListPool.Get();
                    try
                    {
                        foreach ((IMatchmakingQueue queue, DateTime timestamp) in usageData.PreviousQueued)
                        {
                            if (!isDueToCancel && !wasSub && !queue.Options.AllowAutoRequeue)
                                continue;

                            if (Enqueue(group.Leader, group, usageData, queue, isDueToCancel || wasSub ? timestamp : DateTime.UtcNow))
                                addedQueues.Add(queue);
                        }

                        usageData.ClearPreviousQueued();

                        NotifyQueuedAndInvokeChangeCallbacks(null, group, addedQueues, false);

                        foreach (var queue in addedQueues)
                        {
                            if (allAddedQueues.Contains(queue))
                                continue;

                            allAddedQueues.Add(queue);
                        }
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

        string IMatchmakingQueues.NextCommandName => CommandNames.Next;
        string IMatchmakingQueues.CancelCommandName => CommandNames.CancelNext;

        #endregion

        #region IPlayerGroupAdvisor

        bool IPlayerGroupAdvisor.AllowSendInvite(IPlayerGroup group, StringBuilder message)
        {
            if (group is null || !_groupUsageDictionary.TryGetValue(group, out UsageData? usageData))
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
            if (player is null || !player.TryGetExtraData(_puKey, out UsageData? usageData))
                return true;

            if (usageData.State == QueueState.Playing)
            {
                message?.Append("Cannot accept an invite while playing in a match. To accept, complete the current match.");
                return false;
            }

            return true;
        }

        #endregion

        #region Persist

        private void Persist_GetData(Player? player, Stream outStream)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            // Note: Purposely not refreshing the timestamp here since we're on the persist thread and don't want to affect the UsageData.
            DateTime? playHoldExpire = playerData.PlayHoldExpireTimestamp;
            if (playHoldExpire is null)
                return;

            TimeSpan remainingDuration = DateTime.UtcNow - playHoldExpire.Value;
            if (remainingDuration <= TimeSpan.Zero)
                return;

            try
            {
                Persist.Protobuf.MatchmakingQueuesPlayerData protoPlayerData = new()
                {
                    PlayHoldDuration = Duration.FromTimeSpan(remainingDuration)
                };

                protoPlayerData.WriteTo(outStream);
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Warn, nameof(MatchmakingQueues), $"Error serializing MatchmakingQueuesPlayerData. {ex}");
                return;
            }
        }

        private void Persist_SetData(Player? player, Stream inStream)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            Persist.Protobuf.MatchmakingQueuesPlayerData protoPlayerData;

            try
            {
                protoPlayerData = Persist.Protobuf.MatchmakingQueuesPlayerData.Parser.ParseFrom(inStream);
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Warn, nameof(MatchmakingQueues), $"Error deserializing MatchmakingQueuesPlayerData. {ex}");
                return;
            }

            // Set the hold.
            playerData.SetPlayHold(protoPlayerData.PlayHoldDuration.ToTimeSpan());

            // Note: This does not update the player's UsageData since this happening on the persist thread.
            // Usage will be updated later in PlayerActionCallback, when it is on the mainloop thread.
        }

        private void Persist_ClearData(Player? player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return;

            playerData.Reset();
        }

        #endregion

        #region Callbacks

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.Connect)
            {
                if (!player.TryGetExtraData(_puKey, out UsageData? usageData))
                    return;

                if (_playersPlaying.Contains(player.Name!))
                {
                    // The player disconnected while playing in a match that is still ongoing, but has now reconnected.
                    usageData.SetPlaying(false);
                }
                else
                {
                    if (GetRefreshedPlayHold(player) > DateTime.UtcNow)
                    {
                        // The player has a play hold that persists (data came from the persist module).
                        usageData.SetPlaying(false);
                    }

                    if (_pendingPlayHolds.Remove(player.Name!, out TimeSpan duration))
                    {
                        // The player has returned and has a pending play hold.
                        // This is not from the persist module since the player disconnected before the hold was placed.
                        SetPlayHold(player, duration);
                    }
                }
            }
            else if (action == PlayerAction.Disconnect)
            {
                // Remove the player from all queues.
                // Note: If the player was in a group that was queued, the group will remove the player and fire the PlayerGroupMemberRemovedCallback.
                if (!player.TryGetExtraData(_puKey, out UsageData? usageData))
                    return;

                if (usageData.Queued.Count > 0)
                {
                    // The player is searching for a match, stop the search.
                    RemoveFromAllQueues(player, null, usageData, false);
                }

                DateTime? expiration = GetRefreshedPlayHold(player);
                if (expiration is not null)
                {
                    TimeSpan remainingDuration = expiration.Value - DateTime.UtcNow;
                    if (remainingDuration > TimeSpan.Zero)
                    {
                        // The player disconnecting has a play hold.
                        // Store it, so that we have it if the player returns.
                        _pendingPlayHolds[player.Name!] = remainingDuration;
                    }
                }
            }
        }

        private void Callback_PlayerGroupMemberAdded(IPlayerGroup group, Player player)
        {
            if (!player.TryGetExtraData(_puKey, out UsageData? usageData))
                return;

            if (usageData.State == QueueState.Queued)
            {
                // The player was individually searching for a match. Cancel the search now that they've joined a group.
                RemoveFromAllQueues(player, null, usageData, true);
            }
        }

        private void Callback_PlayerGroupMemberRemoved(IPlayerGroup group, Player player, PlayerGroupMemberRemovedReason reason)
        {
            if (!_groupUsageDictionary.TryGetValue(group, out UsageData? usageData))
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
            if (_groupUsageDictionary.Remove(group, out UsageData? usageData))
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
            Args = "<none> | <queue name>[, <queue name>[, ...]]] | [ -status | -s ] | [ -list | -l ] | [-listall | -la] | [-auto | -a ]",
            Description = """
                Control matchmaking for you, or your group.

                To begin searching for a match, specify 1 or more queue names.
                Or, use without any parameters to begin searching on your arena's default queue.

                Use -status (or -s) to print your current matchmaking status.
                Use -list (or -l) to list queue information.
                  When searching, shows only your active queues with your position and wait time.
                  Use -listall or -la to show all queues regardless of search status.
                Use -auto (or -a) to toggle automatic re-queuing (automatically search again after completing a match).
                """)]
        private void Command_next(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (player.Status != PlayerState.Playing || player.Arena is null)
                return;

            // Check if the player is in a group.
            IPlayerGroup? group = _playerGroups.GetGroup(player);

            // Get the usage data.
            UsageData? usageData;
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
                if (!player.TryGetExtraData(_puKey, out usageData))
                    return;
            }

            if (parameters.Equals("-status", StringComparison.OrdinalIgnoreCase) || parameters.Equals("-s", StringComparison.OrdinalIgnoreCase))
            {
                // Print usage details.
                StringBuilder sb;
                switch (usageData.State)
                {
                    case QueueState.None:
                        _chat.SendMessage(player, $"{CommandNames.Next}: {(group is null ? "You are" : "Your group is")} not searching for a game yet.");
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

                            _chat.SendMessage(player, $"{CommandNames.Next}: {(group is null ? "You are" : "Your group is")} searching for a game on the following queues: {sb}");
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
                            if (group is null)
                            {
                                DateTime now = DateTime.UtcNow;
                                DateTime? playHoldExpire = GetRefreshedPlayHold(player);
                                TimeSpan duration = playHoldExpire is null ? TimeSpan.Zero : playHoldExpire.Value - now;

                                if (duration > TimeSpan.Zero)
                                {
                                    sb.Append($"{CommandNames.Next}: You can't search for a game due to dropping out of one recently. Remaining time: ");
                                    sb.AppendFriendlyTimeSpan(duration);
                                    _chat.SendMessage(player, sb);
                                    return;
                                }
                            }

                            foreach (var advisor in player.Arena.GetAdvisors<IMatchmakingQueueAdvisor>())
                            {
                                if (advisor.TryGetCurrentMatchInfo(player.Name!, sb))
                                {
                                    break;
                                }
                            }

                            if (sb.Length > 0)
                            {
                                _chat.SendMessage(player, $"{CommandNames.Next}: {(group is null ? "You are" : "Your group is")} currently playing in a match: {sb}");
                            }
                            else
                            {
                                _chat.SendMessage(player, $"{CommandNames.Next}: {(group is null ? "You are" : "Your group is")} currently playing in a match.");
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

                                    _chat.SendMessage(player, $"{CommandNames.Next}: {(group is null ? "You are" : "Your group is")} set to automatically requeue to: {sb}");
                                }
                                finally
                                {
                                    _objectPoolManager.StringBuilderPool.Return(sb);
                                }
                            }
                            else
                            {
                                _chat.SendMessage(player, $"{CommandNames.Next}: {(group is null ? "You are" : "Your group is")} set to automatically requeue.");
                            }
                        }
                        break;

                    default:
                        break;
                }

                return;
            }
            else if (parameters.Equals("-list", StringComparison.OrdinalIgnoreCase) || parameters.Equals("-l", StringComparison.OrdinalIgnoreCase)
                || parameters.StartsWith("-list ", StringComparison.OrdinalIgnoreCase) || parameters.StartsWith("-l ", StringComparison.OrdinalIgnoreCase)
                || parameters.Contains("-listall", StringComparison.OrdinalIgnoreCase) || parameters.Contains("-la", StringComparison.OrdinalIgnoreCase))
            {
                // Check for the -listall : show all queues even when the player is actively searching.
                bool verbose = parameters.Contains("-listall", StringComparison.OrdinalIgnoreCase) || parameters.Contains("-la", StringComparison.OrdinalIgnoreCase);

                bool isSearching = usageData.State == QueueState.Queued;

                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                HashSet<Player> soloPlayers = _objectPoolManager.PlayerSetPool.Get();
                HashSet<IPlayerGroup> groups = _playerGroupSetPool.Get();

                try
                {
                    if (isSearching && !verbose)
                    {
                        // Show only the queues the player is actively in, with position and wait time.
                        DateTime now = DateTime.UtcNow;
                        foreach (QueuedInfo queuedInfo in usageData.Queued)
                        {
                            IMatchmakingQueue queue = queuedInfo.Queue;
                            sb.Clear();
                            sb.Append($"{CommandNames.Next}: {queue.Name,8} - ");

                            if (queue.TryGetPosition(player, group, out int position, out int totalEntries))
                            {
                                sb.Append($"#{position} of {totalEntries} entr{(totalEntries == 1 ? "y" : "ies")}");
                            }
                            else
                            {
                                sb.Append($"{totalEntries} entr{(totalEntries == 1 ? "y" : "ies")}");
                            }

                            TimeSpan waited = now - queuedInfo.Timestamp;
                            sb.Append(" (waited ");
                            sb.AppendFriendlyTimeSpan(waited);
                            sb.Append(')');

                            if (!string.IsNullOrWhiteSpace(queue.Description))
                                sb.Append($" - {queue.Description}");

                            _chat.SendMessage(player, sb);
                        }
                    }
                    else
                    {
                        // Show all queues with general occupancy info.
                        foreach (IMatchmakingQueue queue in _queues.Values)
                        {
                            sb.Clear();
                            soloPlayers.Clear();
                            groups.Clear();

                            queue.GetQueued(soloPlayers, groups);

                            int totalPlayersQueued = soloPlayers.Count;
                            foreach (IPlayerGroup playerGroup in groups)
                            {
                                totalPlayersQueued += playerGroup.Members.Count;
                            }

                            sb.Append($"{CommandNames.Next}: {queue.Name,8} - {totalPlayersQueued,2} player{(totalPlayersQueued == 1 ? " " : "s")}");

                            if (groups.Count > 0)
                            {
                                sb.Append($" ({groups.Count} group{(groups.Count == 1 ? "" : "s")})");
                            }

                            if (!string.IsNullOrWhiteSpace(queue.Description))
                                sb.Append($" - {queue.Description}");

                            _chat.SendMessage(player, sb);
                        }
                    }
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                    _objectPoolManager.PlayerSetPool.Return(soloPlayers);
                    _playerGroupSetPool.Return(groups);
                }

                return;
            }
            else if (parameters.Equals("-auto", StringComparison.OrdinalIgnoreCase) || parameters.Equals("-a", StringComparison.OrdinalIgnoreCase))
            {
                if (group is not null)
                {
                    if (usageData.AutoRequeue)
                    {
                        usageData.AutoRequeue = false;

                        // Notify the group.
                        HashSet<Player> members = _objectPoolManager.PlayerSetPool.Get();
                        try
                        {
                            members.UnionWith(group.Members);
                            _chat.SendSetMessage(members, $"{CommandNames.Next}: Automatic requeuing disabled by {player.Name}.");
                        }
                        finally
                        {
                            _objectPoolManager.PlayerSetPool.Return(members);
                        }
                    }
                    else
                    {
                        if (group.Leader != player)
                        {
                            _chat.SendMessage(player, $"{CommandNames.Next}: Only the group leader can enable automatic requeuing.");
                        }
                        else
                        {
                            usageData.AutoRequeue = true;

                            // Notify the group.
                            HashSet<Player> members = _objectPoolManager.PlayerSetPool.Get();
                            try
                            {
                                members.UnionWith(group.Members);
                                _chat.SendSetMessage(members, $"{CommandNames.Next}: Automatic requeuing enabled by {player.Name}.");
                            }
                            finally
                            {
                                _objectPoolManager.PlayerSetPool.Return(members);
                            }
                        }
                    }
                }
                else
                {
                    usageData.AutoRequeue = !usageData.AutoRequeue;
                    _chat.SendMessage(player, $"{CommandNames.Next}: Automatic requeuing {(usageData.AutoRequeue ? "enabled" : "disabled")}.");
                }

                return;
            }

            // The command is to start a search.

            if (group is null)
            {
                DateTime now = DateTime.UtcNow;
                DateTime? playHoldExpire = GetRefreshedPlayHold(player);
                TimeSpan duration = playHoldExpire is null ? TimeSpan.Zero : playHoldExpire.Value - now;

                if (duration > TimeSpan.Zero)
                {
                    StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                    try
                    {
                        sb.Append($"{CommandNames.Next}: You can't search for a game due to dropping out of one recently. Remaining time: ");
                        sb.AppendFriendlyTimeSpan(duration);
                        _chat.SendMessage(player, sb);
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                    }

                    return;
                }
            }
            else
            {
                if (player != group.Leader)
                {
                    _chat.SendMessage(player, $"{CommandNames.Next}: Only the group leader can start a search.");
                    return;
                }

                if (group.PendingMembers.Count > 0)
                {
                    _chat.SendMessage(player, $"{CommandNames.Next}: Can't start a search while there are pending invites to the group.");
                    return;
                }

                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                try
                {
                    // Notify if a member has a hold.
                    int holdCount = 0;
                    foreach (Player member in group.Members)
                    {
                        if (GetRefreshedPlayHold(member) is not null)
                        {
                            if (sb.Length > 0)
                                sb.Append(", ");

                            sb.Append(member.Name);
                            holdCount++;
                        }
                    }

                    if (holdCount == 1)
                    {
                        _chat.SendMessage(player, $"{CommandNames.Next}: Can't start a search because {sb} is currently has a hold due to dropping out of a match.");
                        return;
                    }
                    else if (holdCount > 1)
                    {
                        _chat.SendMessage(player, $"{CommandNames.Next}: Can't start a search because the following members have a hold due to dropping out of a match: {sb}");
                        return;
                    }

                    // Notify if a member is playing.
                    int playingCount = 0;
                    foreach (Player member in group.Members)
                    {
                        if (member.TryGetExtraData(_puKey, out UsageData? memberUsage)
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
                        _chat.SendMessage(player, $"{CommandNames.Next}: Can't start a search because {sb} is currently playing.");
                        return;
                    }
                    else if (playingCount > 1)
                    {
                        _chat.SendMessage(player, $"{CommandNames.Next}: Can't start a search because the following members are currently playing: {sb}");
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
                _chat.SendMessage(player, $"{CommandNames.Next}: Can't start a search while playing in a match.");
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
                    _chat.SendMessage(player, $"{CommandNames.Next}: You must specify which queue(s) to search on.");
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

                    IMatchmakingQueue? queue = AddToQueue(player, group, usageData, queueName);
                    if (queue is not null)
                    {
                        addedQueues.Add(queue);
                    }
                }

                NotifyQueuedAndInvokeChangeCallbacks(player, group, addedQueues, true);
            }
            finally
            {
                _iMatchmakingQueueListPool.Return(addedQueues);
            }

            IMatchmakingQueue? AddToQueue(Player player, IPlayerGroup? group, UsageData usageData, string queueName)
            {
                if (!_queues.TryGetValue(queueName, out IMatchmakingQueue? queue))
                {
                    // Did not find a queue with the name provided.
                    // Check if the name provided is an alias.
                    Arena? arena = player.Arena;
                    if (arena is not null)
                    {
                        var advisors = arena.GetAdvisors<IMatchmakingQueueAdvisor>();
                        foreach (IMatchmakingQueueAdvisor advisor in advisors)
                        {
                            string? alias = advisor.GetQueueNameByAlias(arena, queueName);
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
                    _chat.SendMessage(player, $"{CommandNames.Next}: Queue '{queueName}' not found.");
                    return null;
                }

                if (!Enqueue(player, group, usageData, queue, DateTime.UtcNow))
                    return null;

                return queue;
            }
        }

        private bool Enqueue(Player player, IPlayerGroup? group, UsageData usageData, IMatchmakingQueue queue, DateTime timestamp)
        {
            if (group is not null)
            {
                // group search
                if (!queue.Options.AllowGroups)
                {
                    _chat.SendMessage(player, $"{CommandNames.Next}: Queue '{queue.Name}' does not allow premade groups.");
                    return false;
                }

                if (group.Members.Count < queue.Options.MinGroupSize
                    || group.Members.Count > queue.Options.MaxGroupSize)
                {
                    if (queue.Options.MinGroupSize == queue.Options.MaxGroupSize)
                        _chat.SendMessage(player, $"{CommandNames.Next}: Queue '{queue.Name}' allows groups with exactly {queue.Options.MinGroupSize} players, but your group has {group.Members.Count} players.");
                    else
                        _chat.SendMessage(player, $"{CommandNames.Next}: Queue '{queue.Name}' allows groups sized from {queue.Options.MinGroupSize} to {queue.Options.MaxGroupSize} players, but your group has {group.Members.Count} players.");

                    return false;
                }

                if (queue.Options.PermitLeagueId is not null && _leagueAuthorization is not null)
                {
                    StringBuilder namesBuilder = _objectPoolManager.StringBuilderPool.Get();
                    try
                    {
                        foreach (Player groupPlayer in group.Members)
                        {
                            if (!_leagueAuthorization.IsInRole(player.Name!, queue.Options.PermitLeagueId.Value, LeagueRole.PracticePermit))
                            {
                                if (namesBuilder.Length > 0)
                                    namesBuilder.Append(", ");

                                namesBuilder.Append(groupPlayer.Name);
                            }
                        }

                        if (namesBuilder.Length > 0)
                        {
                            StringBuilder messageBuilder = _objectPoolManager.StringBuilderPool.Get();
                            try
                            {
                                messageBuilder.Append($"{CommandNames.Next}: A permit is required for queue '{queue.Name}'. The following group members do not have access: ");
                                messageBuilder.Append(namesBuilder);
                                _chat.SendMessage(player, messageBuilder);
                                return false;
                            }
                            finally
                            {
                                _objectPoolManager.StringBuilderPool.Return(messageBuilder);
                            }
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(namesBuilder);
                    }
                }

                if (!usageData.AddQueue(queue, timestamp))
                {
                    _chat.SendMessage(player, $"{CommandNames.Next}: Already searching for a game on queue '{queue.Name}'.");
                    return false;
                }
                else if (!queue.Add(group, timestamp))
                {
                    usageData.RemoveQueue(queue);
                    _chat.SendMessage(player, $"{CommandNames.Next}: Error adding to the '{queue.Name}' queue.");
                    return false;
                }

                return true;
            }
            else
            {
                // solo search
                if (!queue.Options.AllowSolo)
                {
                    _chat.SendMessage(player, $"{CommandNames.Next}: Queue '{queue.Name}' does not allow solo play. Create or join a group first.");
                    return false;
                }

                if (queue.Options.PermitLeagueId is not null && _leagueAuthorization is not null)
                {
                    if (!_leagueAuthorization.IsInRole(player.Name!, queue.Options.PermitLeagueId.Value, LeagueRole.PracticePermit))
                    {
                        _chat.SendMessage(player, $"{CommandNames.Next}: You do not have permission to use queue '{queue.Name}'. To get access, use: ?leaguepermit request {queue.Name}");
                        return false;
                    }
                }

                if (!usageData.AddQueue(queue, timestamp))
                {
                    _chat.SendMessage(player, $"{CommandNames.Next}: Already searching for a game on queue '{queue.Name}'.");
                    return false;
                }
                else if (!queue.Add(player, timestamp))
                {
                    usageData.RemoveQueue(queue);
                    _chat.SendMessage(player, $"{CommandNames.Next}: Error adding to the '{queue.Name}' queue.");
                    return false;
                }

                return true;
            }
        }

        private void NotifyQueuedAndInvokeChangeCallbacks(Player? player, IPlayerGroup? group, List<IMatchmakingQueue> addedQueues, bool invokeQueueChangedCallbacks)
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

                        _chat.SendSetMessage(members, $"{CommandNames.Next}: Started searching for a game on queue{((addedQueues.Count == 1) ? "" : "s")}: {sb}.");
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(members);
                    }
                }
                else if (player is not null)
                {
                    // Notify the player that a search has started.
                    _chat.SendMessage(player, $"{CommandNames.Next}: Started searching for a game on queue{((addedQueues.Count == 1) ? "" : "s")}: {sb}.");
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }

            if (invokeQueueChangedCallbacks)
            {
                // Fire the callbacks in the order that the queues were added.
                // This will cause matchmaking modules to attempt matches in that order.
                ScheduleQueueChangedCallbacks(addedQueues, QueueAction.Add);
            }
        }

        private void ScheduleQueueChangedCallbacks(List<IMatchmakingQueue> queueList, QueueAction action)
        {
            if (queueList is null)
                return;

            foreach (IMatchmakingQueue queue in queueList)
            {
                ScheduleQueueChangedCallbacks(queue, action);
            }
        }

        private void ScheduleQueueChangedCallbacks(IMatchmakingQueue queue, QueueAction action)
        {
            if (queue is null)
                return;

            foreach (QueueChangeRecord record in _changesQueue)
            {
                if (record.Queue == queue && record.Action == action)
                    return; // already queued
            }

            _changesQueue.Enqueue(new QueueChangeRecord(queue, action));

            // Schedule it to be processed asynchronously.
            _mainloop.QueueMainWorkItem(_mainloopWork_InvokeQueueChangeCallback, (object?)null);
        }

        private void MainloopWork_InvokeQueueChangeCallback(object? dummy)
        {
            if (!_changesQueue.TryDequeue(out QueueChangeRecord record))
                return;

            MatchmakingQueueChangedCallback.Fire(_broker, record.Queue, record.Action);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<none> | [<queue name>[, <queue name>[, ...]]]",
            Description = """
                Cancels a matchmaking search.
                Use the command without specifying a <queue name> to remove from all matchmaking queues.
                """)]
        private void Command_cancelnext(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            // Check if the player is in a group.
            IPlayerGroup? group = _playerGroups.GetGroup(player);

            // Get the usage data.
            UsageData? usageData;
            if (group is not null)
            {
                if (player != group.Leader)
                {
                    _chat.SendMessage(player, $"{CommandNames.CancelNext}: Only the group leader can cancel a search.");
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
                if (!player.TryGetExtraData(_puKey, out usageData))
                    return;
            }

            if (parameters.IsWhiteSpace())
            {
                if (usageData.State == QueueState.Playing && usageData.AutoRequeue)
                {
                    usageData.AutoRequeue = false;
                    _chat.SendMessage(player, $"{CommandNames.CancelNext}: Automatic requeuing disabled.");
                    return;
                }

                if (usageData.Queued.Count == 0)
                {
                    _chat.SendMessage(player, $"{CommandNames.CancelNext}: There are no active searches to cancel.");
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

                    if (!_queues.TryGetValue(queueName, out IMatchmakingQueue? queue))
                    {
                        // Did not find a queue with the name provided.
                        // Check if the name provided is an alias.
                        Arena? arena = player.Arena;
                        if (arena is not null)
                        {
                            var advisors = arena.GetAdvisors<IMatchmakingQueueAdvisor>();
                            foreach (IMatchmakingQueueAdvisor advisor in advisors)
                            {
                                string? alias = advisor.GetQueueNameByAlias(arena, queueName);
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
                        _chat.SendMessage(player, $"{CommandNames.CancelNext}: Queue '{queueName}' not found.");
                        continue;
                    }

                    if (!usageData.RemoveQueue(queue))
                    {
                        _chat.SendMessage(player, $"{CommandNames.CancelNext}: There is no active search to cancel on queue '{queue.Name}'.");
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
                            _chat.SendSetMessage(members, $"{CommandNames.CancelNext}: Search stopped on queue: {queue.Name}.");
                        }
                        finally
                        {
                            _objectPoolManager.PlayerSetPool.Return(members);
                        }
                    }
                    else
                    {
                        _chat.SendMessage(player, $"{CommandNames.CancelNext}: Search stopped on queue: {queue.Name}.");
                    }

                    ScheduleQueueChangedCallbacks(queue, QueueAction.Remove);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "<none> | -r",
            Description = """
                Gets the amount of time a player currently has to wait before being allowed to re-queue with the ?next command.
                Staff members can use -r to remove the target player's hold.
                """)]
        private void Command_nextHold(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
                targetPlayer = player;

            if (!targetPlayer.TryGetExtraData(_pdKey, out PlayerData? targetPlayerData))
                return;

            if (parameters.Contains("-r", StringComparison.OrdinalIgnoreCase))
            {
                if (_capabilityManager.HasCapability(player, Constants.Capabilities.IsStaff)) // TODO: maybe add a capability specifically for this?
                {
                    bool removed = targetPlayerData.RemovePlayHold();

                    if (removed)
                    {
                        if (targetPlayer.TryGetExtraData(_puKey, out UsageData? usageData))
                        {
                            usageData.UnsetPlaying(out _);
                        }

                        _chat.SendMessage(player, $"{CommandNames.NextHold}: Removed hold from {targetPlayer.Name}.");
                        return;
                    }
                }
            }
            else
            {
                DateTime now = DateTime.UtcNow;
                DateTime? playHoldExpire = GetRefreshedPlayHold(targetPlayer);
                TimeSpan duration = playHoldExpire is null ? TimeSpan.Zero : playHoldExpire.Value - now;

                if (duration > TimeSpan.Zero)
                {
                    _chat.SendMessage(player, $"{CommandNames.NextHold}: {(targetPlayer == player ? "You" : targetPlayer.Name)} will be able to use ?{CommandNames.Next} after: {duration}");
                }
                else
                {
                    _chat.SendMessage(player, $"{CommandNames.NextHold}: There is no hold on {(targetPlayer == player ? "you" : targetPlayer.Name)}.");
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = "<none> | -r",
            Description = """
                For staff members to investigate and work-around when a player gets stuck in the "Already playing in a match" state.
                When used with no args, it prints out whether the player is in the playing state.
                When used with -r it removes the playing state from the targeted player.
                """)]
        private void Command_manageplayingstate(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                _chat.SendMessage(player, $"{CommandNames.ManagePlayingState}: A target player is required.");
                return;
            }

            if (!targetPlayer.TryGetExtraData(_puKey, out UsageData? usageData))
                return;

            if (parameters.Equals("-r", StringComparison.OrdinalIgnoreCase))
            {
                if (usageData.State == QueueState.Playing)
                {
                    UnsetPlaying(player, false, false);
                    _chat.SendMessage(player, $"{CommandNames.ManagePlayingState}: Unset {targetPlayer.Name} from the {QueueState.Playing} state.");
                }
            }

            // No matter what, always output the player's state.
            _chat.SendMessage(player, $"{CommandNames.ManagePlayingState}: {targetPlayer.Name} is in the '{usageData.State}' state.");
        }

        #endregion

        private void RemoveFromAllQueues(Player? player, IPlayerGroup? group, UsageData usageData, bool notify)
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

                            _chat.SendSetMessage(members, $"{CommandNames.CancelNext}: Search stopped.");
                        }
                        finally
                        {
                            _objectPoolManager.PlayerSetPool.Return(members);
                        }
                    }
                    else if (player is not null)
                    {
                        _chat.SendMessage(player, $"{CommandNames.CancelNext}: Search stopped.");
                    }
                }

                // Fire the callback.
                ScheduleQueueChangedCallbacks(removedFrom, QueueAction.Remove);
            }
            finally
            {
                _iMatchmakingQueueListPool.Return(removedFrom);
            }
        }

        private void SetPlayHold(Player player, TimeSpan duration)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? playerData)
                || !player.TryGetExtraData(_puKey, out UsageData? usageData))
            {
                return;
            }

            if (playerData.SetPlayHold(duration))
            {
                usageData.SetPlaying(false);
            }
        }

        private DateTime? GetRefreshedPlayHold(Player player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? playerData))
                return null;

            DateTime? expiration = playerData.GetRefreshedPlayHold(out bool removed);

            if (removed && player.TryGetExtraData(_puKey, out UsageData? usageData))
            {
                usageData.UnsetPlaying(out _);
            }

            return expiration;
        }

        #region Helper types

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

        private class PlayerData : IResettable
        {
            /// <summary>
            /// The timestamp that the player will be held in 'Playing' state due to being penalized (e.g. for leaving a game without a sub).
            /// </summary>
            private DateTime? _playHoldExpireTimestamp;

            /// <summary>
            /// For synchronization since this object is accessed by multiple threads, the mainloop thread and the persist thread.
            /// </summary>
            private readonly Lock _lock = new();

            /// <summary>
            /// Gets the play hold expiration time without refreshing it.
            /// </summary>
            public DateTime? PlayHoldExpireTimestamp
            {
                get
                {
                    lock (_lock)
                    {
                        return _playHoldExpireTimestamp;
                    }
                }
            }

            /// <summary>
            /// Refreshes the play hold expiration time and gets it.
            /// </summary>
            /// <param name="removed">Whether the expiration time was removed due to it expiring.</param>
            /// <returns>The expiration time.</returns>
            public DateTime? GetRefreshedPlayHold(out bool removed)
            {
                removed = false;

                lock (_lock)
                {
                    if (_playHoldExpireTimestamp is not null && _playHoldExpireTimestamp.Value <= DateTime.UtcNow)
                    {
                        _playHoldExpireTimestamp = null;
                        removed = true;
                    }

                    return _playHoldExpireTimestamp;
                }
            }

            /// <summary>
            /// Sets the play hold expiration time.
            /// </summary>
            /// <param name="duration">How long from the current time the hold should last.</param>
            /// <returns><see langword="true"/> if the hold was set. <see langword="false"/> if there already is a hold that includes the <paramref name="duration"/>.</returns>
            public bool SetPlayHold(TimeSpan duration)
            {
                DateTime expireTimestamp = DateTime.UtcNow + duration;

                lock (_lock)
                {
                    if (_playHoldExpireTimestamp is null
                        || _playHoldExpireTimestamp.Value < expireTimestamp)
                    {
                        _playHoldExpireTimestamp = expireTimestamp;
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// Removes the play hold.
            /// </summary>
            /// <returns><see langword="true"/> if there was a hold and it was removed. <see langword="false"/> if there was no hold to remove.</returns>
            public bool RemovePlayHold()
            {
                lock (_lock)
                {
                    if (_playHoldExpireTimestamp is not null)
                    {
                        _playHoldExpireTimestamp = null;
                        return true;
                    }
                }

                return false;
            }

            public void Reset()
            {
                lock (_lock)
                {
                    _playHoldExpireTimestamp = null;
                }
            }

            bool IResettable.TryReset()
            {
                Reset();
                return true;
            }
        }

        private readonly record struct QueuedInfo(IMatchmakingQueue Queue, DateTime Timestamp);

        private class UsageData : IResettable
        {
            public QueueState State { get; private set; } = QueueState.None;
            public bool AutoRequeue = false;
            public bool IsPlayingAsSub = false;

            public readonly List<QueuedInfo> Queued = new(8);
            public readonly List<QueuedInfo> PreviousQueued = new(8);

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

            bool IResettable.TryReset()
            {
                State = QueueState.None;
                AutoRequeue = false;
                IsPlayingAsSub = false;
                Queued.Clear();
                PreviousQueued.Clear();
                return true;
            }
        }

        private readonly record struct QueueChangeRecord(IMatchmakingQueue Queue, QueueAction Action)
        {
            public readonly IMatchmakingQueue Queue = Queue ?? throw new ArgumentNullException(nameof(Queue));
            public readonly QueueAction Action = Action;
        }

        private static class CommandNames
        {
            public const string Next = "next";
            public const string CancelNext = "cancelnext";
            public const string CancelNextAlt = "cnext";
            public const string NextHold = "nexthold";
            public const string ManagePlayingState = "manageplayingstate";
        }

        #endregion
    }
}

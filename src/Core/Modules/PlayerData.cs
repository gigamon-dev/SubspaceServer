using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that manages <see cref="Player"/> objects.
    /// </summary>
    [CoreModuleInfo]
    public class PlayerData : IModule, IPlayerData
    {
        internal ComponentBroker Broker;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private InterfaceRegistrationToken<IPlayerData> _iPlayerDataToken;

        /// <summary>
        /// How long after a PlayerID can be reused after it is freed.
        /// </summary>
        private readonly static TimeSpan PlayerReuseDelay = TimeSpan.FromSeconds(10);

        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.SupportsRecursion);

        /// <summary>
        /// The next available PlayerId for when a <see cref="Player"/> object needs to be allocated.
        /// </summary>
        private int _nextPlayerId = 0;

        /// <summary>
        /// Dictionary of active players.
        /// </summary>
        /// <remarks>Key =  PlayerId</remarks>
        private readonly Dictionary<int, Player> _playerDictionary = new(256);

        /// <summary>
        /// Players queued to be freed that are in a state where they've been removed from the <see cref="_playerDictionary"/>, but are not yet in the <see cref="_freePlayersQueue"/>.
        /// Player objects in this collection still have their extra data.
        /// </summary>
        private readonly HashSet<Player> _playersBeingFreed = new(256);

        /// <summary>
        /// Queue of unused player objects and when each becomes available again.
        /// This is used to keep track of the PlayerIds (and associated <see cref="Player"/> object) can be reused and when.
        /// </summary>
        private readonly Queue<FreePlayerInfo> _freePlayersQueue = new(256);

        // for managing per player data
        private readonly SortedList<int, ExtraDataFactory> _extraDataRegistrations = new();
        private readonly DefaultObjectPoolProvider _poolProvider = new() { MaximumRetained = 256 };

        #region Module Members

        public bool Load(
            ComponentBroker broker, 
            ILogManager logManager,
            IMainloop mainloop)
        {
            Broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));

            _iPlayerDataToken = broker.RegisterInterface<IPlayerData>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iPlayerDataToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IPlayerData Members

        #region Locks

        /// <summary>
        /// Locks global player data for reading
        /// </summary>
        public void Lock()
        {
            _rwLock.EnterReadLock();
        }

        public void Unlock()
        {
            _rwLock.ExitReadLock();
        }

        /// <summary>
        /// Locks global player data for writing
        /// </summary>
        public void WriteLock()
        {
            _rwLock.EnterWriteLock();
        }

        public void WriteUnlock()
        {
            _rwLock.ExitWriteLock();
        }

        #endregion

        Dictionary<int, Player>.ValueCollection IPlayerData.Players => _playerDictionary.Values;

        Player IPlayerData.NewPlayer(ClientType clientType)
        {
            Player player;

            WriteLock();

            try
            {
                if (_freePlayersQueue.TryPeek(out FreePlayerInfo free)
                    && DateTime.UtcNow > free.AvailableTimestamp)
                {
                    // reuse an existing player object that is now available
                    free = _freePlayersQueue.Dequeue();
                    player = free.Player;
                }
                else
                {
                    // no available player objects, create a new one
                    player = new Player(_nextPlayerId++, this);
                }

                // set player info
                player.Status = PlayerState.Uninitialized;
                player.Type = clientType;
                player.Arena = null;
                player.NewArena = null;
                player.Ship = ShipType.Spec;
                player.Attached = -1;
                player.ConnectTime = DateTime.UtcNow;
                player.ConnectAs = null;

                // initialize the player's per player data
                foreach ((int keyId, ExtraDataFactory factory) in _extraDataRegistrations)
                {
                    player.SetExtraData(keyId, factory.Get());
                }

                _playerDictionary.Add(player.Id, player);
            }
            finally
            {
                WriteUnlock();
            }

            _mainloop.QueueMainWorkItem(MainloopWork_FireNewPlayerCallback, player);

            return player;

            void MainloopWork_FireNewPlayerCallback(Player player)
            {
                NewPlayerCallback.Fire(Broker, player, true);
            }
        }

        void IPlayerData.FreePlayer(Player player)
        {
            if (player is null)
                return;

            WriteLock();

            try
            {
                // Remove the player from the active players.
                if (!_playerDictionary.Remove(player.Id))
                    return;

                // Add the player to the players being freed.
                if (!_playersBeingFreed.Add(player))
                    return;

                // Queue the player to be freed.
                _mainloop.QueueMainWorkItem(MainloopWork_CompleteFreePlayer, player);
            }
            finally
            {
                WriteUnlock();
            }

            void MainloopWork_CompleteFreePlayer(Player player)
            {
                // First, execute callbacks.
                // This allows other modules to perform cleanup, including their extra player data.
                NewPlayerCallback.Fire(Broker, player, false);

                // Next, do the "freeing".
                WriteLock();

                try
                {
                    if (!_playersBeingFreed.Remove(player))
                        return;

                    // Remove the extra player data.
                    foreach ((int keyId, ExtraDataFactory info) in _extraDataRegistrations)
                    {
                        if (player.TryRemoveExtraData(keyId, out object data))
                        {
                            info.Return(data);
                        }
                    }

                    // Reset the player's data back to their initial values.
                    player.Initialize();

                    // Add the player to the queue, "freeing" it to be reused after a configured amount of time.
                    _freePlayersQueue.Enqueue(new FreePlayerInfo(player));
                }
                finally
                {
                    WriteUnlock();
                }
            }
        }

        void IPlayerData.KickPlayer(Player player)
        {
            if (player is null)
                return;

            WriteLock();

            try
            {
                // this will set state to PlayerState.LeavingArena, if it was anywhere above PlayerState.LoggedIn
                if (player.Arena is not null)
                {
                    IArenaManagerInternal aman = Broker.GetInterface<IArenaManagerInternal>();
                    if (aman is not null)
                    {
                        try
                        {
                            aman.LeaveArena(player);
                        }
                        finally
                        {
                            Broker.ReleaseInterface(ref aman);
                        }
                    }
                }

                // set this special flag so that the player will be set to leave
                // the zone when the PlayerState.LeavingArena-initiated actions are completed
                player.WhenLoggedIn = PlayerState.LeavingZone;
            }
            finally
            {
                WriteUnlock();
            }
        }

        Player IPlayerData.PidToPlayer(int pid)
        {
            Lock();

            try
            {
                _playerDictionary.TryGetValue(pid, out Player player);
                return player;
            }
            finally
            {
                Unlock();
            }
        }

        Player IPlayerData.FindPlayer(ReadOnlySpan<char> name)
        {
            name = name.Trim();

            Lock();

            try
            {
                foreach (Player player in _playerDictionary.Values)
                {
                    if (name.Equals(player.Name, StringComparison.OrdinalIgnoreCase)
                        // this is a sort of hackish way of not returning players who are on their way out.
                        && player.Status < PlayerState.LeavingZone
                        && player.WhenLoggedIn < PlayerState.LeavingZone)
                    {
                        return player;
                    }
                }
            }
            finally
            {
                Unlock();
            }

            return null;
        }

        void IPlayerData.TargetToSet(ITarget target, HashSet<Player> set)
        {
            if (target is null)
                throw new ArgumentNullException(nameof(target));

            if (set is null)
                throw new ArgumentNullException(nameof(set));

            switch (target.Type)
            {
                case TargetType.Player:
                    set.Add(((IPlayerTarget)target).Player);
                    return;

                case TargetType.Arena:
                case TargetType.Freq:
                case TargetType.Zone:
                    Lock();
                    try
                    {
                        foreach (Player p in _playerDictionary.Values)
                        {
                            if ((p.Status == PlayerState.Playing) && Matches(target, p))
                                set.Add(p);
                        }
                    }
                    finally
                    {
                        Unlock();
                    }
                    return;

                case TargetType.Set:
                    set.UnionWith(((ISetTarget)target).Players);
                    return;

                case TargetType.None:
                default:
                    return;
            }

            static bool Matches(ITarget target, Player player)
            {
                if (target is null || player is null)
                    return false;

                return target.Type switch
                {
                    TargetType.Arena => player.Arena == ((IArenaTarget)target).Arena,
                    TargetType.Freq => (target is ITeamTarget teamTarget) && (player.Arena == teamTarget.Arena) && (player.Freq == teamTarget.Freq),
                    TargetType.Zone => true,
                    _ => false,
                };
            }
        }

        PlayerDataKey<T> IPlayerData.AllocatePlayerData<T>()
        {
            // Only use of a pool of T objects if there's a way for the objects to be [re]initialized.
            if (typeof(T).IsAssignableTo(typeof(IPooledExtraData)))
                return new PlayerDataKey<T>(AllocatePlayerData(() => new DefaultPooledExtraDataFactory<T>(_poolProvider)));
            else
                return new PlayerDataKey<T>(AllocatePlayerData(() => new NonPooledExtraDataFactory<T>()));
        }

        PlayerDataKey<T> IPlayerData.AllocatePlayerData<T>(IPooledObjectPolicy<T> policy) where T : class
        {
            if (policy is null)
                throw new ArgumentNullException(nameof(policy));

            // It's the policy's job to clear/reset an object when it's returned to the pool.
            return new PlayerDataKey<T>(AllocatePlayerData(() => new CustomPooledExtraDataFactory<T>(_poolProvider, policy)));
        }

        private int AllocatePlayerData(Func<ExtraDataFactory> createExtraDataFactoryFunc)
        {
            if (createExtraDataFactoryFunc is null)
                throw new ArgumentNullException(nameof(createExtraDataFactoryFunc));

            WriteLock();

            try
            {
                //
                // Register
                //

                int keyId;

                // find next available
                for (keyId = 1; keyId <= _extraDataRegistrations.Keys.Count; keyId++)
                {
                    if (_extraDataRegistrations.Keys[keyId-1] != keyId)
                        break;
                }

                ExtraDataFactory factory = createExtraDataFactoryFunc();
                _extraDataRegistrations[keyId] = factory;

                //
                // Add the data to each player.
                //

                foreach (Player player in _playerDictionary.Values)
                {
                    player.SetExtraData(keyId, factory.Get());
                }

                return keyId;
            }
            finally
            {
                WriteUnlock();
            }
        }

        bool IPlayerData.FreePlayerData<T>(ref PlayerDataKey<T> key)
        {
            if (key.Id == 0)
            {
                _logManager.LogM(LogLevel.Warn, nameof(ArenaManager), $"There was an attempt to FreeArenaData with an uninitialized key (Id = 0).");
                return false;
            }

            WriteLock();

            try
            {
                //
                // Unregister
                //

                if (!_extraDataRegistrations.Remove(key.Id, out ExtraDataFactory factory))
                    return false;

                //
                // Remove the data from every player
                //

                foreach (Player player in _playerDictionary.Values)
                {
                    if (player.TryRemoveExtraData(key.Id, out object data))
                    {
                        factory.Return(data);
                    }
                }

                foreach (Player player in _playersBeingFreed)
                {
                    if (player.TryRemoveExtraData(key.Id, out object data))
                    {
                        factory.Return(data);
                    }
                }

                factory.Dispose();
            }
            finally
            {
                WriteUnlock();
            }

            key = new(0);
            return true;
        }

        #endregion

        #region Helper Types

        private readonly struct FreePlayerInfo
        {
            /// <summary>
            /// The time the associated player object (predominantly its Id) will be available.
            /// </summary>
            public readonly DateTime AvailableTimestamp;

            /// <summary>
            /// The player object
            /// </summary>
            public readonly Player Player;

            public FreePlayerInfo(Player player)
            {
                AvailableTimestamp = DateTime.UtcNow + PlayerReuseDelay;
                Player = player;
            }
        }

        #endregion
    }
}

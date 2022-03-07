using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Threading;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class PlayerData : IModule, IPlayerData
    {
        internal ComponentBroker Broker;
        private InterfaceRegistrationToken<IPlayerData> _iPlayerDataToken;

        /// <summary>
        /// How long after a PlayerID can be reused after it is freed.
        /// </summary>
        private readonly static TimeSpan PlayerReuseDelay = TimeSpan.FromSeconds(10);

        private readonly ReaderWriterLockSlim _globalPlayerDataRwLock = new(LockRecursionPolicy.SupportsRecursion);

        private int _nextPlayerId = 0;

        /// <summary>
        /// Dictionary to look up players by Id.
        /// Doubles as the list of players that can be enumerated on.
        /// </summary>
        private readonly Dictionary<int, Player> _playerDictionary = new(256);

        /// <summary>
        /// Queue of unused player objects and when each becomes available again.
        /// This is used to keep track of the PlayerIds (and associated <see cref="Player"/> object) can be reused and when.
        /// </summary>
        private readonly Queue<FreePlayerInfo> _freePlayersQueue = new(256);

        // for managing per player data
        private readonly SortedList<int, ExtraDataFactory> _perPlayerDataRegistrations = new();
        private readonly DefaultObjectPoolProvider _poolProvider = new() { MaximumRetained = 256 };

        #region Module Members

        public bool Load(ComponentBroker broker)
        {
            Broker = broker ?? throw new ArgumentNullException(nameof(broker));
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
            _globalPlayerDataRwLock.EnterReadLock();
        }

        public void Unlock()
        {
            _globalPlayerDataRwLock.ExitReadLock();
        }

        /// <summary>
        /// Locks global player data for writing
        /// </summary>
        public void WriteLock()
        {
            _globalPlayerDataRwLock.EnterWriteLock();
        }

        public void WriteUnlock()
        {
            _globalPlayerDataRwLock.ExitWriteLock();
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
                foreach ((int keyId, ExtraDataFactory info) in _perPlayerDataRegistrations)
                {
                    player[new PlayerDataKey(keyId)] = info.Get();
                }

                _playerDictionary.Add(player.Id, player);
            }
            finally
            {
                WriteUnlock();
            }

            NewPlayerCallback.Fire(Broker, player, true);

            return player;
        }

        void IPlayerData.FreePlayer(Player player)
        {
            if (player == null)
                return;

            NewPlayerCallback.Fire(Broker, player, false);

            WriteLock();

            try
            {
                // remove the player from the dictionary
                _playerDictionary.Remove(player.Id);

                foreach ((int keyId, ExtraDataFactory info) in _perPlayerDataRegistrations)
                {
                    if (player.TryRemoveExtraData(new PlayerDataKey(keyId), out object data))
                    {
                        info.Return(data);
                    }
                }

                player.Initialize();

                _freePlayersQueue.Enqueue(new FreePlayerInfo(player));
            }
            finally
            {
                WriteUnlock();
            }
        }

        void IPlayerData.KickPlayer(Player player)
        {
            if (player == null)
                return;

            WriteLock();

            try
            {
                // this will set state to PlayerState.LeavingArena, if it was anywhere above PlayerState.LoggedIn
                if (player.Arena != null)
                {
                    IArenaManagerInternal aman = Broker.GetInterface<IArenaManagerInternal>();
                    if (aman != null)
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
            _globalPlayerDataRwLock.EnterReadLock();

            try
            {
                _playerDictionary.TryGetValue(pid, out Player player);
                return player;
            }
            finally
            {
                _globalPlayerDataRwLock.ExitReadLock();
            }
        }

        Player IPlayerData.FindPlayer(ReadOnlySpan<char> name)
        {
            _globalPlayerDataRwLock.EnterReadLock();

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
                _globalPlayerDataRwLock.ExitReadLock();
            }

            return null;
        }

        void IPlayerData.TargetToSet(ITarget target, HashSet<Player> set)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (set == null)
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

            static bool Matches(ITarget t, Player p)
            {
                if (t == null || p == null)
                    return false;

                return t.Type switch
                {
                    TargetType.Arena => p.Arena == ((IArenaTarget)t).Arena,
                    TargetType.Freq => (t is ITeamTarget teamTarget) && (p.Arena == teamTarget.Arena) && (p.Freq == teamTarget.Freq),
                    TargetType.Zone => true,
                    _ => false,
                };
            }
        }

        PlayerDataKey IPlayerData.AllocatePlayerData<T>()
        {
            // Only use of a pool of T objects if there's a way for the objects to be [re]initialized.
            return (typeof(T).IsAssignableTo(typeof(IPooledExtraData)))
                ? AllocatePlayerData(() => new DefaultPooledExtraDataFactory<T>(_poolProvider))
                : AllocatePlayerData(() => new NonPooledExtraDataFactory<T>());
        }

        PlayerDataKey IPlayerData.AllocatePlayerData<T>(IPooledObjectPolicy<T> policy) where T : class
        {
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            // It's the policy's job to clear/reset an object when it's returned to the pool.
            return AllocatePlayerData(() => new CustomPooledExtraDataFactory<T>(_poolProvider, policy));
        }

        private PlayerDataKey AllocatePlayerData(Func<ExtraDataFactory> createExtraDataFactoryFunc)
        {
            if (createExtraDataFactoryFunc == null)
                throw new ArgumentNullException(nameof(createExtraDataFactoryFunc));

            WriteLock();

            try
            {
                //
                // Register
                //

                int keyId;

                // find next available
                for (keyId = 0; keyId < _perPlayerDataRegistrations.Keys.Count; keyId++)
                {
                    if (_perPlayerDataRegistrations.ContainsKey(keyId) == false)
                        break;
                }

                PlayerDataKey key = new(keyId);
                ExtraDataFactory factory = createExtraDataFactoryFunc();
                _perPlayerDataRegistrations[keyId] = factory;

                //
                // Add the data to each player
                //

                foreach (Player player in _playerDictionary.Values)
                {
                    player[key] = factory.Get();
                }

                return key;
            }
            finally
            {
                WriteUnlock();
            }
        }

        void IPlayerData.FreePlayerData(PlayerDataKey key)
        {
            WriteLock();

            try
            {
                //
                // Unregister
                //

                if (!_perPlayerDataRegistrations.Remove(key.Id, out ExtraDataFactory factory))
                    return;

                //
                // Remove the data from every player
                //

                foreach (Player player in _playerDictionary.Values)
                {
                    if (player.TryRemoveExtraData(key, out object data))
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
        }

        #endregion

        #region Helper Types

        private class FreePlayerInfo
        {
            /// <summary>
            /// The time the associated player object (predominantly its Id) will be available.
            /// </summary>
            public DateTime AvailableTimestamp;

            /// <summary>
            /// The player object
            /// </summary>
            public Player Player;

            public FreePlayerInfo(Player player)
            {
                Player = player;
                SetAvailableTimestampFromNow();
            }

            public void SetAvailableTimestampFromNow()
            {
                AvailableTimestamp = DateTime.UtcNow + PlayerReuseDelay;
            }
        }

        #endregion
    }
}

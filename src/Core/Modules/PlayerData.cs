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
        private InterfaceRegistrationToken _iPlayerDataToken;

        /// <summary>
        /// How long after a PlayerID can be reused after it is freed.
        /// </summary>
        private readonly static TimeSpan PlayerReuseDelay = TimeSpan.FromSeconds(10);

        private readonly ReaderWriterLockSlim _globalPlayerDataRwLock = new(LockRecursionPolicy.SupportsRecursion);

        private int _nextPid = 0;

        /// <summary>
        /// Dictionary to look up players by Id.
        /// Doubles as the list of players that can be enumerated on (PlayerList).
        /// </summary>
        private readonly Dictionary<int, Player> _playerDictionary = new(256);

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

        /// <summary>
        /// Queue of unused player objects and when each becomes available again.
        /// This is used to keep track of the PlayerIDs (and associated <see cref="Player"/> object) can be reused and when.
        /// </summary>
        private readonly Queue<FreePlayerInfo> _freePlayersQueue = new(256);

        // for managing per player data
        private readonly ReaderWriterLockSlim _perPlayerDataLock = new(LockRecursionPolicy.NoRecursion);
        private readonly SortedList<int, Type> _perPlayerDataKeys = new();

        #region IModule Members

        public bool Load(ComponentBroker broker)
        {
            Broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _iPlayerDataToken = broker.RegisterInterface<IPlayerData>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface<IPlayerData>(ref _iPlayerDataToken) != 0)
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

        private IEnumerable<Player> PlayerList
        {
            get { return _playerDictionary.Values; }
        }

        IEnumerable<Player> IPlayerData.PlayerList
        {
            get { return PlayerList; }
        }

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
                    player = new Player(_nextPid++, this);
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
                _perPlayerDataLock.EnterReadLock();

                try
                {
                    foreach (KeyValuePair<int, Type> kvp in _perPlayerDataKeys)
                    {
                        player[kvp.Key] = Activator.CreateInstance(kvp.Value);
                    }
                }
                finally
                {
                    _perPlayerDataLock.ExitReadLock();
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
            NewPlayerCallback.Fire(Broker, player, false);

            WriteLock();

            try
            {
                // remove the player from the dictionary
                _playerDictionary.Remove(player.Id);

                player.RemoveAllExtraData();

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
                    IArenaManager aman = Broker.GetInterface<IArenaManager>();
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

        Player IPlayerData.FindPlayer(string name)
        {
            _globalPlayerDataRwLock.EnterReadLock();

            try
            {
                foreach (Player player in _playerDictionary.Values)
                {
                    if (string.Equals(player.Name, name, StringComparison.OrdinalIgnoreCase)
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

        void IPlayerData.TargetToSet(ITarget target, out LinkedList<Player> list)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (target.Type == TargetType.List)
            {
                list = new LinkedList<Player>(((IListTarget)target).List);
                return;
            }

            list = new LinkedList<Player>();

            switch (target.Type)
            {
                case TargetType.Player:
                    list.AddLast(((IPlayerTarget)target).Player);
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
                                list.AddLast(p);
                        }
                    }
                    finally
                    {
                        Unlock();
                    }
                    return;

                case TargetType.None:
                default:
                    return;
            }

            static bool Matches(ITarget t, Player p)
            {
                switch (t.Type)
                {
                    case TargetType.Arena:
                        return p.Arena == ((IArenaTarget)t).Arena;

                    case TargetType.Freq:
                        ITeamTarget teamTarget = (ITeamTarget)t;
                        return (p.Arena == teamTarget.Arena) && (p.Freq == teamTarget.Freq);

                    case TargetType.Zone:
                        return true;

                    case TargetType.List:
                    case TargetType.Player:
                    case TargetType.None:
                    default:
                        return false;
                }
            }
        }

        int IPlayerData.AllocatePlayerData<T>()
        {
            int key = 0;

            _perPlayerDataLock.EnterWriteLock();

            try
            {
                // find next available key
                for (key = 0; key < _perPlayerDataKeys.Keys.Count; key++)
                {
                    if (_perPlayerDataKeys.Keys.Contains(key) == false)
                        break;
                }

                _perPlayerDataKeys[key] = typeof(T);
            }
            finally
            {
                _perPlayerDataLock.ExitWriteLock();
            }

            WriteLock();

            try
            {
                foreach (Player player in PlayerList)
                {
                    player[key] = new T();
                }
            }
            finally
            {
                WriteUnlock();
            }

            return key;
        }

        void IPlayerData.FreePlayerData(int key)
        {
            WriteLock();

            try
            {
                foreach (Player player in PlayerList)
                {
                    player.RemoveExtraData(key);
                }
            }
            finally
            {
                WriteUnlock();
            }

            _perPlayerDataLock.EnterWriteLock();

            try
            {
                // remove the key from 
                _perPlayerDataKeys.Remove(key);
            }
            finally
            {
                _perPlayerDataLock.ExitWriteLock();
            }
        }

        #endregion
    }
}

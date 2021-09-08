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
        private ComponentBroker _broker;
        private InterfaceRegistrationToken _iPlayerDataToken;

        /// <summary>
        /// how many seconds before we re-use a pid
        /// </summary>
        private const int PID_REUSE_DELAY = 10;

        private ReaderWriterLock _globalPlayerDataRwLock = new ReaderWriterLock();

        private int _nextPid = 0;

        /// <summary>
        /// Dictionary to look up players by Id.
        /// Doubles as the list of players that can be enumerated on (PlayerList).
        /// </summary>
        private Dictionary<int, Player> _playerDictionary = new Dictionary<int,Player>();

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
                AvailableTimestamp = DateTime.UtcNow.AddSeconds(PID_REUSE_DELAY);
            }
        }

        /// <summary>
        /// List of unused player objects (sort of like a pool).
        /// Players objects are stored in here so that they don't have to be reallocated
        /// every time a player logs off and on.
        /// This doubles as a way to keep track of which Ids are in use too.
        /// </summary>
        private LinkedList<FreePlayerInfo> _freePlayersList = new LinkedList<FreePlayerInfo>();

        /// <summary>
        /// List of unused nodes for use in the free players list.
        /// Nodes are stored in here so that they wont have to be reallocated.
        /// </summary>
        private LinkedList<FreePlayerInfo> _freeNodesList = new LinkedList<FreePlayerInfo>();

        // for managing per player data
        private ReaderWriterLock _perPlayerDataLock = new ReaderWriterLock();
        private SortedList<int, Type> _perPlayerDataKeys = new SortedList<int, Type>();

        public PlayerData()
        {
        }

        #region IModule Members

        public bool Load(ComponentBroker broker)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
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
            _globalPlayerDataRwLock.AcquireReaderLock(Timeout.Infinite);
        }

        public void Unlock()
        {
            _globalPlayerDataRwLock.ReleaseReaderLock();
        }

        /// <summary>
        /// Locks global player data for writing
        /// </summary>
        public void WriteLock()
        {
            _globalPlayerDataRwLock.AcquireWriterLock(Timeout.Infinite);
        }

        public void WriteUnlock()
        {
            _globalPlayerDataRwLock.ReleaseWriterLock();
        }

        #endregion

        private IEnumerable<Player> playerList
        {
            get { return _playerDictionary.Values; }
        }

        IEnumerable<Player> IPlayerData.PlayerList
        {
            get { return playerList; }
        }

        Player IPlayerData.NewPlayer(ClientType clientType)
        {
            Player player;

            WriteLock();

            try
            {
                LinkedListNode<FreePlayerInfo> pidNode = _freePlayersList.First;

                if ((pidNode != null) &&
                    (DateTime.UtcNow > pidNode.Value.AvailableTimestamp))
                {
                    // got an existing player object
                    _freePlayersList.Remove(pidNode);
                    player = pidNode.Value.Player;

                    pidNode.Value.Player = null;
                    _freeNodesList.AddLast(pidNode);
                }
                else
                {
                    // no available player objects
                    player = new Player(_nextPid++);
                }

                // initialize the player's per player data
                _perPlayerDataLock.AcquireReaderLock(Timeout.Infinite);
                try
                {
                    foreach (KeyValuePair<int, Type> kvp in _perPlayerDataKeys)
                    {
                        player[kvp.Key] = Activator.CreateInstance(kvp.Value);
                    }
                }
                finally
                {
                    _perPlayerDataLock.ReleaseReaderLock();
                }

                _playerDictionary.Add(player.Id, player);

                // set player info
                player.Status = PlayerState.Uninitialized;
                player.Type = clientType;
                player.Arena = null;
                player.NewArena = null;
                player.Ship = ShipType.Spec;
                player.Attached = -1;
                player.ConnectTime = DateTime.UtcNow;
                player.ConnectAs = null;
            }
            finally
            {
                WriteUnlock();
            }

            NewPlayerCallback.Fire(_broker, player, true);

            return player;
        }

        void IPlayerData.FreePlayer(Player player)
        {
            NewPlayerCallback.Fire(_broker, player, false);

            WriteLock();

            // remove the player from the dictionary
            _playerDictionary.Remove(player.Id);

            player.RemoveAllPerPlayerData();

            LinkedListNode<FreePlayerInfo> node = _freeNodesList.First;
            if (node != null)
            {
                // got a node
                _freeNodesList.Remove(node);
                node.Value.SetAvailableTimestampFromNow();
                node.Value.Player = player;
            }
            else
            {
                // no free nodes, create one
                node = new LinkedListNode<FreePlayerInfo>(new FreePlayerInfo(player));
            }

            _freePlayersList.AddLast(node);

            WriteUnlock();
        }

        void IPlayerData.KickPlayer(Player player)
        {
            if (player == null)
                return;

            WriteLock();
            try
            {
                // this will set state to S_LEAVING_ARENA, if it was anywhere above S_LOGGEDIN
                if (player.Arena != null)
                {
                    IArenaManager aman = _broker.GetInterface<IArenaManager>();
                    if (aman != null)
                    {
                        try
                        {
                            aman.LeaveArena(player);
                        }
                        finally
                        {
                            _broker.ReleaseInterface(ref aman);
                        }
                    }
                }

                // set this special flag so that the player will be set to leave
                // the zone when the S_LEAVING_ARENA-initiated actions are completed
                player.WhenLoggedIn = PlayerState.LeavingZone;
            }
            finally
            {
                WriteUnlock();
            }
        }

        Player IPlayerData.PidToPlayer(int pid)
        {
            try
            {
                _globalPlayerDataRwLock.AcquireReaderLock(Timeout.Infinite);

                Player player;
                _playerDictionary.TryGetValue(pid, out player);
                return player;
            }
            finally
            {
                _globalPlayerDataRwLock.ReleaseReaderLock();
            }
        }

        Player IPlayerData.FindPlayer(string name)
        {
            try
            {
                _globalPlayerDataRwLock.AcquireReaderLock(Timeout.Infinite);

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
                _globalPlayerDataRwLock.ReleaseReaderLock();
            }

            return null;
        }

        void IPlayerData.TargetToSet(ITarget target, out LinkedList<Player> list)
        {
            if (target == null)
                throw new ArgumentNullException("target");

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
                            if ((p.Status == PlayerState.Playing) && matches(target, p))
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
        }

        int IPlayerData.AllocatePlayerData<T>()
        {
            int key = 0;

            _perPlayerDataLock.AcquireWriterLock(Timeout.Infinite);
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
                _perPlayerDataLock.ReleaseWriterLock();
            }

            Lock();
            try
            {
                foreach (Player player in playerList)
                {
                    player[key] = new T();
                }
            }
            finally
            {
                Unlock();
            }

            return key;
        }

        void IPlayerData.FreePlayerData(int key)
        {
            Lock();
            try
            {
                foreach (Player player in playerList)
                {
                    player.RemovePerPlayerData(key);
                }
            }
            finally
            {
                Unlock();
            }

            _perPlayerDataLock.AcquireWriterLock(Timeout.Infinite);
            try
            {
                // remove the key from 
                _perPlayerDataKeys.Remove(key);
            }
            finally
            {
                _perPlayerDataLock.ReleaseWriterLock();
            }
        }

        #endregion

        // this is is inlined in asss, the compiler should do it for us automatically if it is a good candidate
        private static bool matches(ITarget t, Player p)
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
}

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using SS.Core.Packets;

namespace SS.Core
{
    public interface IPlayerData : IComponentInterface
    {
        void Lock();
        void Unlock();
        void WriteLock();
        void WriteUnlock();

        /// <summary>
        /// Use to enumerate over all of the players.
        /// Rember to Lock() and Unlock().
        /// </summary>
        IEnumerable<Player> PlayerList
        {
            get;
        }

        Player NewPlayer(ClientType clientType);
        void FreePlayer(Player player);
        void KickPlayer(Player player);
        Player PidToPlayer(int pid);
        Player FindPlayer(string name);
        void TargetToSet(Target target, out LinkedList<Player> list);

        // per player data
        int AllocatePlayerData<T>() where T : new();
        void FreePlayerData(int key);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="p">the player being allocated/deallocated</param>
    /// <param name="isNew">true if being allocated, false if being deallocated</param>
    /// <returns></returns>
    public delegate void NewPlayerDelegate(Player p, bool isNew);

    public class PlayerData : IModule, IPlayerData
    {
        /// <summary>
        /// how many seconds before we re-use a pid
        /// </summary>
        private const int PID_REUSE_DELAY = 10;

        private ReaderWriterLock _globalPlayerDataRwLock = new ReaderWriterLock();

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
                AvailableTimestamp = DateTime.Now.AddSeconds(PID_REUSE_DELAY);
            }
        }

        /// <summary>
        /// List of unused player objects (sort of like a pool).
        /// Players objects are stored in here so that they don't have to be reallocated
        /// every time a plays logoff and login.
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

        /// <summary>
        /// this callback is called whenever a Player struct is allocated or
        /// deallocated. in general you probably want to use CB_PLAYERACTION
        /// instead of this callback for general initialization tasks.
        /// </summary>
        public event NewPlayerDelegate NewPlayerEvent;

        public PlayerData()
        {
        }

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get
            {
                return new Type[] {
                    typeof(IConfigManager)
                };
            }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            mm.RegisterInterface<IPlayerData>(this);
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
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

        public IEnumerable<Player> PlayerList
        {
            get
            {
                return _playerDictionary.Values;
            }
        }

        Player IPlayerData.NewPlayer(ClientType clientType)
        {
            Player player;

            WriteLock();

            try
            {
                LinkedListNode<FreePlayerInfo> pidNode = _freePlayersList.First;

                if ((pidNode != null) &&
                    (DateTime.Now > pidNode.Value.AvailableTimestamp))
                {
                    // got an existing player object
                    _freePlayersList.RemoveFirst();
                    player = pidNode.Value.Player;

                    pidNode.Value.Player = null;
                    _freeNodesList.AddLast(pidNode);
                }
                else
                {
                    // no available player objects
                    player = new Player(_playerDictionary.Count);
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
                player.pkt.PktType = (byte)S2CPacketType.PlayerEntering;
                player.Status = PlayerState.Uninitialized;
                player.Type = clientType;
                player.Arena = null;
                player.NewArena = null;
                player.pkt.Ship = (sbyte)ShipType.Spec;
            }
            finally
            {
                WriteUnlock();
            }

            if(NewPlayerEvent != null)
            {
                NewPlayerEvent(player, true);
            }

            return player;
        }

        void IPlayerData.FreePlayer(Player player)
        {
            if (NewPlayerEvent != null)
            {
                NewPlayerEvent(player, false);
            }

            WriteLock();

            // remove the player from the dictionary
            _playerDictionary.Remove(player.Id);

            player.RemoveAllPerPlayerData();

            LinkedListNode<FreePlayerInfo> node = _freeNodesList.First;
            if (node != null)
            {
                // got a node
                _freeNodesList.RemoveFirst();
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
            WriteLock();

            // TODO:

            WriteUnlock();
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
                    if ((string.Compare(player.Name, name, true) == 0) &&
                        player.Status < PlayerState.LeavingZone &&
                        player.WhenLoggedIn < PlayerState.LeavingZone)
                    {
                        return player;
                    }
                }

                return null;
            }
            finally
            {
                _globalPlayerDataRwLock.ReleaseReaderLock();
            }
        }

        void IPlayerData.TargetToSet(Target target, out LinkedList<Player> list)
        {
            if (target.Type == TargetType.List)
            {
                list = new LinkedList<Player>(target.List);
                return;
            }

            list = new LinkedList<Player>();

            switch (target.Type)
            {
                case TargetType.Player:
                    list.AddLast(target.Player);
                    return;

                case TargetType.Arena:
                case TargetType.Freq:
                case TargetType.Zone:
                    Lock();
                    foreach (Player p in _playerDictionary.Values)
                    {
                        if ((p.Status == PlayerState.Playing) && matches(target, p))
                            list.AddLast(p);
                    }
                    Unlock();
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
                foreach (Player player in PlayerList)
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
                foreach (Player player in PlayerList)
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
        private static bool matches(Target t, Player p)
        {
            switch (t.Type)
            {
                case TargetType.Arena:
                    return p.Arena == t.Arena;

                case TargetType.Freq:
                    return (p.Arena == t.Arena) && (p.Freq == t.Freq);

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

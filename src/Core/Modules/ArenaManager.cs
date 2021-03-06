using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class ArenaManager : IModule, IArenaManager, IModuleLoaderAware
    {
        /// <summary>
        /// the read-write lock for the global arena list
        /// </summary>
        private readonly ReaderWriterLock _arenaLock = new ReaderWriterLock();

        private readonly Dictionary<string, Arena> _arenaDictionary = new Dictionary<string, Arena>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Key = module Type
        /// Value = list of arenas that have the module attached
        /// </summary>
        private readonly Dictionary<Type, List<Arena>> _attachedModules = new Dictionary<Type, List<Arena>>();

        private ComponentBroker _broker;
        private IModuleManager _mm;
        private ILogManager _logManager;
        private IPlayerData _playerData;
        private INetwork _net;
        private IConfigManager _configManager;
        private IMainloop _mainloop;
        private IMainloopTimer _mainloopTimer;
        private IServerTimer _serverTimer;
        //private IPersist _persist;
        private InterfaceRegistrationToken _iArenaManagerCoreToken;

        // for managing per arena data
        private readonly ReaderWriterLock _perArenaDataLock = new ReaderWriterLock();
        private readonly SortedList<int, Type> _perArenaDataKeys = new SortedList<int, Type>();

        // population
        private int _playersTotal;
        private int _playersPlaying;
        private DateTime? _populationLastRefreshed;
        private readonly TimeSpan _populationRefreshThreshold = TimeSpan.FromMilliseconds(1000);
        private readonly object _populationRefreshLock = new object();


        private class SpawnLoc
        {
            // TODO: consider using the System.Drawing.Point struct, it might have useful operators/methods
            public short X, Y;

            public SpawnLoc() : this(0, 0)
            {
            }

            public SpawnLoc(short x, short y)
            {
                X = x;
                Y = y;
            }
        }

        /// <summary>
        /// per player data key (SpawnLoc) 
        /// </summary>
        private int _spawnkey;

        private class ArenaData
        {
            /// <summary>
            /// counter for the # of holds on the arena
            /// </summary>
            public int Holds = 0;

            /// <summary>
            /// whether the arena should be recreated after it is destroyed
            /// </summary>
            public bool Resurrect = false;

            public bool Reap = false;
        }

        /// <summary>
        /// per arena data key (ArenaData) 
        /// </summary>
        private int _adkey;

        private static readonly byte[] _brickClearBytes = new byte[1] { (byte)Packets.S2CPacketType.Brick };
        private static readonly byte[] _enteringArenaBytes = new byte[1] { (byte)Packets.S2CPacketType.EnteringArena };

        /// <summary>
        /// all arenas
        /// </summary>
        /// <remarks>remember to use Lock/Unlock</remarks>
        public IEnumerable<Arena> ArenaList
        {
            get
            {
                return _arenaDictionary.Values;
            }
        }

        #region Locks

        void IArenaManager.Lock()
        {
            ReadLock();
        }

        private void ReadLock()
        {
            _arenaLock.AcquireReaderLock(Timeout.Infinite);
        }

        void IArenaManager.Unlock()
        {
            ReadUnlock();
        }

        private void ReadUnlock()
        {
            _arenaLock.ReleaseReaderLock();
        }

        private void WriteLock()
        {
            _arenaLock.AcquireWriterLock(Timeout.Infinite);
        }

        private void WriteUnlock()
        {
            _arenaLock.ReleaseWriterLock();
        }

        #endregion

        #region IArenaManagerCore Members

        void IArenaManager.SendArenaResponse(Player player)
        {
            if (player == null)
                return;

            Arena arena = player.Arena;

            if (arena == null)
            {
                _logManager.LogP(LogLevel.Warn, nameof(ArenaManager), player, "bad arena in SendArenaResponse");
                return;
            }

            _logManager.LogP(LogLevel.Info, nameof(ArenaManager), player, "entering arena");

            if (player.IsStandard)
            {
                // send whoami packet
                using (DataBuffer buffer = Pool<DataBuffer>.Default.Get())
                {
                    SimplePacket whoami = new SimplePacket(buffer.Bytes);
                    whoami.Type = (byte)S2CPacketType.WhoAmI;
                    whoami.D1 = (short)player.Id;
                    _net.SendToOne(player, buffer.Bytes, 3, NetSendFlags.Reliable);
                }
                
                // send settings
                IClientSettings clientset = _broker.GetInterface<IClientSettings>();
                if (clientset != null)
                {
                    try
                    {
                        clientset.SendClientSettings(player);
                    }
                    finally
                    {
                        _broker.ReleaseInterface(ref clientset);
                    }
                }
            }
            else if (player.IsChat)
            {
                // TODO: 
                //_chatnet.SendToOne(player, "INARENA:%s:%d", a.Name, player.Freq);
            }

            _playerData.Lock();
            try
            {
                foreach (Player otherPlayer in _playerData.PlayerList)
                {
                    if (otherPlayer.Status == PlayerState.Playing
                        && otherPlayer.Arena == arena 
                        && otherPlayer != player)
                    {
                        // send each other info
                        SendEnter(otherPlayer, player, true);
                        SendEnter(player, otherPlayer, false);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            if (player.IsStandard)
            {
                // send to self
                _net.SendToOne(player, player.pkt.Bytes, PlayerDataPacket.Length, NetSendFlags.Reliable);

                IMapNewsDownload mapNewDownload = _broker.GetInterface<IMapNewsDownload>();
                if (mapNewDownload != null)
                {
                    try
                    {
                        mapNewDownload.SendMapFilename(player);
                    }
                    finally
                    {
                        _broker.ReleaseInterface(ref mapNewDownload);
                    }
                }

                // send brick clear and finisher
                _net.SendToOne(player, _brickClearBytes, _brickClearBytes.Length, NetSendFlags.Reliable);
                _net.SendToOne(player, _enteringArenaBytes, _enteringArenaBytes.Length, NetSendFlags.Reliable);

                if (player[_spawnkey] is SpawnLoc sp)
                {
                    if ((sp.X > 0) && (sp.Y > 0) && (sp.X < 1024) && (sp.Y < 1024))
                    {
                        using (DataBuffer buffer = Pool<DataBuffer>.Default.Get())
                        {
                            SimplePacket wto = new SimplePacket(buffer.Bytes);

                            wto.Type = (byte)S2CPacketType.WarpTo;
                            wto.D1 = sp.X;
                            wto.D2 = sp.Y;
                            _net.SendToOne(player, buffer.Bytes, 5, NetSendFlags.Reliable);
                        }
                    }
                }
            }
        }

        void IArenaManager.LeaveArena(Player player)
        {
            LeaveArena(player);
        }

        private bool InitiateLeaveArena(Player player)
        {
            bool notify = false;

            /* this messy logic attempts to deal with players who haven't fully
             * entered an arena yet. it will try to insert them at the proper
             * stage of the arena leaving process so things that have been done
             * get undone, and things that haven't been done _don't_ get undone. */
            switch (player.Status)
            {
                case PlayerState.LoggedIn:
                case PlayerState.DoFreqAndArenaSync:
                    //for these 2, nothing much has been done. just go back to loggedin.
                    player.Status = PlayerState.LoggedIn;
                    break;

                case PlayerState.WaitArenaSync1:
                    /* this is slightly tricky: we want to wait until persist is
                     * done loading the scores before changing the state, or
                     * things will get screwed up. so mark it here and let core
                     * take care of it. this is really messy and it would be
                     * nice to find a better way to handle it. */
                    player.Flags.LeaveArenaWhenDoneWaiting = true;
                    break;

                case PlayerState.ArenaRespAndCBS:
                    // in these, stuff has come out of the database. put it back in
                    player.Status = PlayerState.DoArenaSync2;
                    break;

                case PlayerState.Playing:
                    // do all of the above, plus call leaving callbacks.
                    player.Status = PlayerState.LeavingArena;
                    notify = true;
                    break;

                case PlayerState.LeavingArena:
                case PlayerState.DoArenaSync2:
                case PlayerState.WaitArenaSync2:
                case PlayerState.LeavingZone:
                case PlayerState.WaitGlobalSync2:
                    //no problem, player is already on the way out
                    break;

                default:
                    // something's wrong here
                    Console.WriteLine("player [{0}] has an arena, but in bad state [{1}]", player.Name, player.Status.ToString());
                    notify = true;
                    break;
            }

            return notify;
        }

        #endregion

        bool IArenaManager.RecycleArena(Arena arena)
        {
            WriteLock();
            try
            {
                if (arena.Status != ArenaState.Running)
                    return false;

                _playerData.WriteLock();
                try
                {
                    foreach (Player player in _playerData.PlayerList)
                    {
                        if (player.Arena == arena &&
                            !player.IsStandard &&
                            !player.IsChat)
                        {
                            _logManager.LogA(LogLevel.Warn, nameof(ArenaManager), arena, "can't recycle arena with fake players");
                            return false;
                        }
                    }

                    using (DataBuffer buffer = Pool<DataBuffer>.Default.Get())
                    {
                        SimplePacket whoami = new SimplePacket(buffer.Bytes);
                        whoami.Type = (byte)S2CPacketType.WhoAmI;

                        // first move playing players elsewhere
                        foreach (Player player in _playerData.PlayerList)
                        {
                            if (player.Arena == arena)
                            {
                                if(player.IsStandard)
                                {
                                    whoami.D1 = (short)player.Id;
                                    _net.SendToOne(player, buffer.Bytes, 3, NetSendFlags.Reliable);
                                }
                                else if (player.IsChat)
                                {
                                    //_chatNet.SendToOne(
                                }

                                // actually initiate the client leaving arena on our side
                                InitiateLeaveArena(player);

                                // and mark the same arena as his desired arena to enter
                                player.NewArena = arena;
                            }
                        }
                    }
                }
                finally
                {
                    _playerData.WriteUnlock();
                }

                arena.Status = ArenaState.Closing;

                if (arena[_adkey] is ArenaData arenaData)
                    arenaData.Resurrect = true;

                return true;
            }
            finally
            {
                WriteUnlock();
            }
        }

        void IArenaManager.SendToArena(Player player, string arenaName, int spawnx, int spawny)
        {
            switch(player.Type)
            {
                case ClientType.Continuum:
                case ClientType.VIE:
                    CompleteGo(
                        player, 
                        arenaName, 
                        player.Ship, 
                        player.Xres, 
                        player.Yres, 
                        player.Flags.WantAllLvz, 
                        player.pkt.AcceptAudio != 0, 
                        player.Flags.ObscenityFilter, 
                        spawnx, 
                        spawny);
                    break;

                case ClientType.Chat:
                    CompleteGo(
                        player,
                        arenaName,
                        ShipType.Spec,
                        0,
                        0,
                        false,
                        false,
                        player.Flags.ObscenityFilter,
                        0,
                        0);
                    break;
            }
        }

        private Arena FindArena(string name)
        {
            ReadLock();
            try
            {
                return DoFindArena(name, ArenaState.Running, ArenaState.Running);
            }
            finally
            {
                ReadUnlock();
            }
        }

        Arena IArenaManager.FindArena(string name)
        {
            return FindArena(name);
        }

        Arena IArenaManager.FindArena(string name, out int totalCount, out int playing)
        {
            Arena arena = FindArena(name);

            if (arena != null)
            {
                CountPlayers(arena, out totalCount, out playing);
            }
            else
            {
                totalCount = 0;
                playing = 0;
            }

            return arena;
        }

        private Arena DoFindArena(string name, ArenaState? minState, ArenaState? maxState)
        {
            ReadLock();
            try
            {
                if (_arenaDictionary.TryGetValue(name, out Arena arena) == false)
                    return null;

                if (minState != null && arena.Status < minState)
                    return null;

                if (maxState != null && arena.Status > maxState)
                    return null;

                return arena;
            }
            finally
            {
                ReadUnlock();
            }
        }

        private void CountPlayers(Arena arena, out int total, out int playing)
        {
            int totalCount = 0;
            int playingCount = 0;

            _playerData.Lock();
            try
            {
                foreach (Player player in _playerData.PlayerList)
                {
                    if (player.Status == PlayerState.Playing &&
                        player.Arena == arena &&
                        player.Type != ClientType.Fake)
                    {
                        totalCount++;

                        if (player.Ship != ShipType.Spec)
                            playingCount++;
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            total = totalCount;
            playing = playingCount;
        }

        private void CompleteGo(Player player, string reqName, ShipType ship, int xRes, int yRes, bool gfx, bool voices, bool obscene, int spawnX, int spawnY)
        {
            // status should be LoggedIn or Playing at this point
            if (player.Status != PlayerState.LoggedIn && player.Status != PlayerState.Playing && player.Status != PlayerState.LeavingArena)
            {
                _logManager.LogP(LogLevel.Warn, nameof(ArenaManager), player, "state sync problem: sent arena request from bad status ({0})", player.Status);
                return;
            }

            // remove all illegal characters and make lowercase
            StringBuilder sb = new StringBuilder(reqName);
            for (int x = 0; x < sb.Length; x++)
            {
                if (x == 0 && sb[x] == '#')
                    continue;
                else if(!char.IsLetterOrDigit(sb[x]))
                    sb[x] = 'x';
                else if(char.IsUpper(sb[x]))
                    sb[x] = char.ToLower(sb[x]);
            }

            string name;
            if (sb.Length == 0)
            {
                IArenaPlace ap = _broker.GetInterface<IArenaPlace>();
                if (ap != null)
                {
                    try
                    {
                        int spx = 0, spy = 0;
                        ap.Place(out name, ref spx, ref spy, player);
                    }
                    finally
                    {
                        _broker.ReleaseInterface(ref ap);
                    }
                }
                else
                {
                    name = "0";
                }
            }
            else
            {
                name = sb.ToString();
            }

            if (player.Arena != null)
                LeaveArena(player);

            // try to locate an existing arena
            WriteLock();
            try
            {
                Arena arena = DoFindArena(name, ArenaState.DoInit0, ArenaState.DoDestroy2);
                if (arena == null)
                {
                    // create a non-permanent arena
                    arena = CreateArena(name, false);
                    if (arena == null)
                    {
                        // if it fails, dump in first available
                        foreach (Arena a in _arenaDictionary.Values)
                        {
                            arena = a;
                            break;
                        }

                        if (arena == null)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(ArenaManager), "internal error: no running arenas but cannot create new one");
                            return;
                        }
                    }
                    else if (arena.Status > ArenaState.Running)
                    {
                        // arena is on it's way out
                        // this isn't a problem, just make sure that it will come back
                        if (!(arena[_adkey] is ArenaData arenaData))
                            return;

                        arenaData.Resurrect = true;
                    }
                }

                // set up player info
                _playerData.WriteLock();
                try
                {
                    player.NewArena = arena;
                }
                finally
                {
                    _playerData.WriteUnlock();
                }
                player.Ship = ship;
                player.Xres = (short)xRes;
                player.Yres = (short)yRes;
                player.Flags.WantAllLvz = gfx;
                player.pkt.AcceptAudio = voices ? (byte)1 : (byte)0;
                player.Flags.ObscenityFilter = obscene;

                if (player[_spawnkey] is SpawnLoc sp)
                {
                    sp.X = (short)spawnX;
                    sp.Y = (short)spawnY;
                }
            }
            finally
            {
                WriteUnlock();
            }

            // don't mess with player status yet, let him stay in S_LOGGEDIN.
            // it will be incremented when the arena is ready.
        }

        private void LeaveArena(Player player)
        {
            bool notify;
            Arena arena;

            _playerData.WriteLock();
            try
            {
                arena = player.Arena;
                if (arena == null)
                    return;

                notify = InitiateLeaveArena(player);
            }
            finally
            {
                _playerData.WriteUnlock();
            }

            if (notify)
            {
                using (DataBuffer buffer = Pool<DataBuffer>.Default.Get())
                {
                    SimplePacket pk = new SimplePacket(buffer.Bytes);

                    pk.Type = (byte)S2CPacketType.PlayerLeaving;
                    pk.D1 = (short)player.Id;

                    _net.SendToArena(arena, player, buffer.Bytes, 3, NetSendFlags.Reliable);
                    //chatnet.SendToArena(
                }

                _logManager.LogP(LogLevel.Info, nameof(ArenaManager), player, "leaving arena");
            }
        }

        void IArenaManager.GetPopulationSummary(out int total, out int playing)
        {
            // Unless I'm missing something, thread synchronization in ASSS doesn't seem right.  
            // a read lock is being held for reading the arena list (supposed to be locked prior to calling this method)
            // a read lock is being held for reading the player list
            // but it's writing to each arena, meaning multiple threads could be writing to Arena.Total and Areana.Playing simultaneously
            // I've added a double checked lock, _populationRefreshLock, which will only allow 1 thread in to refresh the data at a given time.

            // TODO: Can ArenaManager/Arena be enhanced such that an increment/decrement occurs when players enter/leave, change ships, etc?

            if (RefreshNeeded())
            {
                lock (_populationRefreshLock)
                {
                    if (RefreshNeeded())
                    {
                        // refresh population stats
                        ICapabilityManager capman = _broker.GetInterface<ICapabilityManager>();

                        try
                        {
                            _playersTotal = _playersPlaying = 0;

                            foreach (Arena arena in ArenaList)
                            {
                                arena.Total = arena.Playing = 0;
                            }

                            _playerData.Lock();

                            try
                            {
                                foreach (Player p in _playerData.PlayerList)
                                {
                                    if (p.Status == PlayerState.Playing
                                        && p.Type != ClientType.Fake
                                        && p.Arena != null
                                        && (capman == null || !capman.HasCapability(p, Constants.Capabilities.ExcludePopulation)))
                                    {
                                        _playersTotal++;
                                        p.Arena.Total++;

                                        if (p.Ship != ShipType.Spec)
                                        {
                                            _playersPlaying++;
                                            p.Arena.Playing++;
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                _playerData.Unlock();
                            }
                        }
                        finally
                        {
                            if (capman != null)
                                _broker.ReleaseInterface(ref capman);
                        }

                        _populationLastRefreshed = DateTime.UtcNow;
                    }
                }
            }

            total = _playersTotal;
            playing = _playersPlaying;

            bool RefreshNeeded() => _populationLastRefreshed == null || (DateTime.UtcNow - _populationLastRefreshed.Value) >= _populationRefreshThreshold;
        }

        private bool ServerTimer_UpdateKnownArenas()
        {
            WriteLock();

            try
            {
                // TODO: is this really needed? 
            }
            finally
            {
                WriteUnlock();
            }

            return true; // keep running
        }

        int IArenaManager.AllocateArenaData<T>()
        {
            int key = 0;

            _perArenaDataLock.AcquireWriterLock(Timeout.Infinite);
            try
            {
                // find next available key
                for (key = 0; key < _perArenaDataKeys.Keys.Count; key++)
                {
                    if (_perArenaDataKeys.Keys.Contains(key) == false)
                        break;
                }

                _perArenaDataKeys[key] = typeof(T);
            }
            finally
            {
                _perArenaDataLock.ReleaseWriterLock();
            }

            ReadLock();
            try
            {
                foreach (Arena arena in ArenaList)
                {
                    arena[key] = new T();
                }
            }
            finally
            {
                ReadUnlock();
            }

            return key;
        }

        void IArenaManager.FreeArenaData(int key)
        {
            ReadLock();
            try
            {
                foreach (Arena arena in ArenaList)
                {
                    arena.RemovePerArenaData(key);
                }
            }
            finally
            {
                ReadUnlock();
            }

            _perArenaDataLock.AcquireWriterLock(Timeout.Infinite);
            try
            {
                _perArenaDataKeys.Remove(key);
            }
            finally
            {
                _perArenaDataLock.ReleaseWriterLock();
            }
        }

        void IArenaManager.HoldArena(Arena arena)
        {
            WriteLock();
            try
            {
                switch(arena.Status)
                {
                    case ArenaState.WaitHolds0:
                    case ArenaState.WaitHolds1:
                    case ArenaState.WaitHolds2:
                        if (!(arena[_adkey] is ArenaData arenaData))
                            return;

                        arenaData.Holds++;
                        break;

                    default:
                        _logManager.LogA(LogLevel.Error, nameof(ArenaManager), arena, "Hold called from invalid state");
                        break;
                }
            }
            finally
            {
                WriteUnlock();
            }
        }

        void IArenaManager.UnholdArena(Arena arena)
        {
            WriteLock();
            try
            {
                switch (arena.Status)
                {
                    case ArenaState.WaitHolds0:
                    case ArenaState.WaitHolds1:
                    case ArenaState.WaitHolds2:
                        if (!(arena[_adkey] is ArenaData arenaData))
                            return;

                        if (arenaData.Holds > 0)
                        {
                            arenaData.Holds--;
                        }
                        break;

                    default:
                        _logManager.LogA(LogLevel.Error, nameof(ArenaManager), arena, "Unhold called from invalid state");
                        break;
                }
            }
            finally
            {
                WriteUnlock();
            }
        }

        [ConfigHelp("Modules", "AttachModules", ConfigScope.Arena, typeof(string), 
            Description = "This is a list of modules that you want to take effect in this" +
            "arena. Not all modules need to be attached to arenas to function, but some do.")]
        private void DoAttach(Arena a)
        {
            string attachMods = _configManager.GetStr(a.Cfg, "Modules", "AttachModules");
            if (string.IsNullOrWhiteSpace(attachMods))
                return;

            string[] attachModsArray = attachMods.Split("\t:;".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);

            foreach (string moduleToAttach in attachModsArray)
            {
                _mm.AttachModule(moduleToAttach, a);
            }
        }

        private void ArenaConfChanged(object clos)
        {
            if (!(clos is Arena arena))
                return;

            ReadLock();
            try
            {
                // only running arenas should receive confchanged events
                if (arena.Status == ArenaState.Running)
                {
                    OnArenaAction(arena, ArenaAction.ConfChanged);
                }
            }
            finally
            {
                ReadUnlock();
            }
        }

        private void SendEnter(Player player, Player playerTo, bool already)
        {
            if (playerTo.IsStandard)
            {
                _net.SendToOne(playerTo, player.pkt.Bytes, PlayerDataPacket.Length, NetSendFlags.Reliable);
            }
            else if (playerTo.IsChat)
            {
                //_chatNet.SendToOne(
            }
        }

        /*
        // this is for persist
        private void arenaSyncDone(Arena arena)
        {
            writeLock();
            try
            {

                switch(arena.Status)
                {
                    case ArenaState.WaitSync1:
                        arena.Status = ArenaState.Running;
                        break;

                    case ArenaState.WaitSync2:
                        arena.Status = ArenaState.DoDestroy1;
                        break;

                    default:
                        _logManager.LogA(LogLevel.Warn, "arenaman", arena, "arena_sync_done called from wrong state");
                        break;
                }
            }
            finally
            {
                writeUnlock();
            }
        }
        */

        protected virtual void OnArenaAction(Arena arena, ArenaAction action)
        {
            if (arena != null)
                ArenaActionCallback.Fire(arena, arena, action);
            else
                ArenaActionCallback.Fire(_broker, arena, action);
        }

        private bool MainloopTimer_ProcessArenaStates()
        {
            WriteLock();
            try
            {
                foreach (Arena arena in ArenaList)
                {
                    ArenaData arenaData = arena[_adkey] as ArenaData;
                    ArenaState status = arena.Status;

                    switch (status)
                    {
                        case ArenaState.WaitHolds0:
                            if (arenaData.Holds == 0)
                                status = arena.Status = ArenaState.DoInit1;
                            break;

                        case ArenaState.WaitHolds1:
                            if (arenaData.Holds == 0)
                                status = arena.Status = ArenaState.DoInit2;
                            break;

                        case ArenaState.WaitHolds2:
                            if (arenaData.Holds == 0)
                                status = arena.Status = ArenaState.DoDestroy2;
                            break;
                    }

                    switch (status)
                    {
                        case ArenaState.DoInit0:
                            arena.Cfg = _configManager.OpenConfigFile(arena.BaseName, null, ArenaConfChanged, arena);
                            arena.SpecFreq = (short)_configManager.GetInt(arena.Cfg, "Team", "SpectatorFrequency", Arena.DEFAULT_SPEC_FREQ);
                            arena.Status = ArenaState.WaitHolds0;
                            Debug.Assert(arenaData.Holds == 0);
                            OnArenaAction(arena, ArenaAction.PreCreate);
                            break;

                        case ArenaState.DoInit1:
                            DoAttach(arena);
                            arena.Status = ArenaState.WaitHolds1;
                            Debug.Assert(arenaData.Holds == 0);
                            OnArenaAction(arena, ArenaAction.Create);
                            break;

                        case ArenaState.DoInit2:
                            // TODO: create the persist interface
                            //if (persist != null)
                            //{
                            //}
                            //else
                                arena.Status = ArenaState.Running;
                            break;

                        case ArenaState.DoWriteData:
                            bool hasPlayers = false;
                            _playerData.Lock();
                            try
                            {
                                foreach (Player p in _playerData.PlayerList)
                                {
                                    if (p.Arena == arena)
                                    {
                                        hasPlayers = true;
                                        break;
                                    }
                                }
                            }
                            finally
                            {
                                _playerData.Unlock();
                            }

                            if (hasPlayers == false)
                            {
                                /* TODO: create the persist interface
                                if (persist != null)
                                {
                                    persist.PutArena(arena, arenaSyncDone);
                                    arena.Status = ArenaState.WaitSync2;
                                }
                                else
                                {
                                */
                                    arena.Status = ArenaState.DoDestroy1;
                                //}
                            }
                            else
                            {
                                // let's not destroy this after all
                                arena.Status = ArenaState.Running;
                            }
                            break;

                        case ArenaState.DoDestroy1:
                            arena.Status = ArenaState.WaitHolds2;
                            Debug.Assert(arenaData.Holds == 0);
                            OnArenaAction(arena, ArenaAction.Destroy);
                            break;

                        case ArenaState.DoDestroy2:
                            if (_mm.DetachAllFromArena(arena))
                            {
                                _configManager.CloseConfigFile(arena.Cfg);
                                arena.Cfg = null;
                                OnArenaAction(arena, ArenaAction.PostDestroy);

                                if (arenaData.Resurrect)
                                {
                                    // clear all private data on recycle, so it looks to modules like it was just created.
                                    _perArenaDataLock.AcquireReaderLock(Timeout.Infinite);
                                    try
                                    {
                                        foreach (KeyValuePair<int, Type> kvp in _perArenaDataKeys)
                                        {
                                            arena.RemovePerArenaData(kvp.Key);
                                            arena[kvp.Key] = Activator.CreateInstance(kvp.Value);
                                        }
                                    }
                                    finally
                                    {
                                        _perArenaDataLock.ReleaseReaderLock();
                                    }

                                    arenaData.Resurrect = false;
                                    arena.Status = ArenaState.DoInit0;
                                }
                                else
                                {
                                    _arenaDictionary.Remove(arena.Name);

                                    // make sure that any work associated with the arena that is to run on the mainloop is complete
                                    _mainloop.WaitForMainWorkItemDrain();

                                    return true; // kinda hacky, but we can't enumerate again if we modify the dictionary
                                }
                            }
                            else
                            {
                                _logManager.LogA(LogLevel.Error, nameof(ArenaManager), arena, "Failed to detach modules from arena, arena will not be destroyed. Check for correct interface releasing.");
                                _arenaDictionary.Remove(arena.Name);
                                _arenaDictionary.Add(Guid.NewGuid().ToString(), arena);
                                _logManager.LogA(LogLevel.Error, nameof(ArenaManager), arena, "WARNING: the server is no longer in a stable state because of this error. your modules need to be fixed.");

                                // TODO: flush logs

                                arenaData.Resurrect = false;
                                arenaData.Reap = false;
                                //TODO: arena.KeepAlive = true;
                                arena.Status = ArenaState.Running;

                            }
                            break;
                    }
                }
            }
            finally
            {
                WriteUnlock();
            }

            return true;
        }

        // call with writeLock held
        private Arena CreateArena(string name, bool permanent)
        {
            Arena arena = new Arena(_broker, name);
            arena.KeepAlive = permanent;

            _perArenaDataLock.AcquireReaderLock(Timeout.Infinite);
            try
            {
                foreach (KeyValuePair<int, Type> kvp in _perArenaDataKeys)
                {
                    arena[kvp.Key] = Activator.CreateInstance(kvp.Value);
                }
            }
            finally
            {
                _perArenaDataLock.ReleaseReaderLock();
            }

            WriteLock();
            try
            {
                _arenaDictionary.Add(name, arena);
            }
            finally
            {
                WriteUnlock();
            }

            _logManager.LogA(LogLevel.Info, nameof(ArenaManager), arena, "created arena");

            return arena;
        }

        private bool ReapArenas()
        {
            ReadLock();
            try
            {
                _playerData.Lock();
                try
                {
                    foreach (Arena arena in ArenaList)
                    {
                        ArenaData arenaData = arena[_adkey] as ArenaData;
                        arenaData.Reap = arena.Status == ArenaState.Running || arena.Status == ArenaState.Closing;
                    }

                    foreach (Player player in _playerData.PlayerList)
                    {
                        if (player.Arena != null)
                        {
                            ArenaData arenaData = player.Arena[_adkey] as ArenaData;
                            arenaData.Reap = false;
                        }

                        if (player.NewArena != null && player.Arena != player.NewArena)
                        {
                            ArenaData arenaData = player.NewArena[_adkey] as ArenaData;
                            if (player.NewArena.Status == ArenaState.Closing)
                            {
                                arenaData.Resurrect = true;
                            }
                            else
                            {
                                arenaData.Reap = false;
                            }
                        }
                    }

                    foreach (Arena arena in ArenaList)
                    {
                        ArenaData arenaData = arena[_adkey] as ArenaData;

                        if (arenaData.Reap && (arena.Status == ArenaState.Closing || !arena.KeepAlive))
                        {
                            _logManager.LogA(LogLevel.Drivel, nameof(ArenaManager), arena, 
                                $"arena being {((arena.Status == ArenaState.Running) ? "destroyed" : "recycled")}");

                            // set its status so that the arena processor will do appropriate things
                            arena.Status = ArenaState.DoWriteData;
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
            finally
            {
                ReadUnlock();
            }

            return true;
        }

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IModuleManager mm,
            ILogManager log,
            IPlayerData playerData,
            INetwork net,
            IConfigManager configManager,
            IMainloop mainloop,
            IMainloopTimer mainloopTimer, 
            IServerTimer serverTimer)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _mm = mm ?? throw new ArgumentNullException(nameof(mm));
            _logManager = log ?? throw new ArgumentNullException(nameof(log));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _net = net ?? throw new ArgumentNullException(nameof(net));
            //_chatnet = 
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _serverTimer = serverTimer ?? throw new ArgumentNullException(nameof(serverTimer));

            _spawnkey = _playerData.AllocatePlayerData<SpawnLoc>();

            IArenaManager amc = this;
            _adkey = amc.AllocateArenaData<ArenaData>();

            _net.AddPacket(C2SPacketType.GotoArena, Packet_GotoArena);
            _net.AddPacket(C2SPacketType.LeaveArena, Packet_LeaveArena);

            // TODO: 
            //if(_chatnet)
            //{
            //}

            _mainloopTimer.SetTimer(MainloopTimer_ProcessArenaStates, 100, 100, null);
            _mainloopTimer.SetTimer(ReapArenas, 1700, 1700, null);
            _serverTimer.SetTimer(ServerTimer_UpdateKnownArenas, 0, 1000, null);

            _iArenaManagerCoreToken = _broker.RegisterInterface<IArenaManager>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<IArenaManager>(ref _iArenaManagerCoreToken) != 0)
                return false;

            _net.RemovePacket(C2SPacketType.GotoArena, Packet_GotoArena);
            _net.RemovePacket(C2SPacketType.LeaveArena, Packet_LeaveArena);

            // TODO: 
            //if(_chatnet)
            //{
            //}

            _serverTimer.ClearTimer(ServerTimer_UpdateKnownArenas, null);
            _mainloopTimer.ClearTimer(MainloopTimer_ProcessArenaStates, null);
            _mainloopTimer.ClearTimer(ReapArenas, null);

            _playerData.FreePlayerData(_spawnkey);

            IArenaManager amc = this;
            amc.FreeArenaData(_spawnkey);
            amc.FreeArenaData(_adkey);

            _arenaDictionary.Clear();

            return true;
        }

        #endregion

        #region IModuleLoaderAware Members

        [ConfigHelp("Arenas", "PermanentArenas", ConfigScope.Global, typeof(string), 
            "A list of the names of arenas to permanently set up when the server is started.")]
        bool IModuleLoaderAware.PostLoad(ComponentBroker broker)
        {
            //_persist = mm.GetInterface<IPersist>();

            string permanentArenas = _configManager.GetStr(_configManager.Global, "Arenas", "PermanentArenas");
            if (!string.IsNullOrWhiteSpace(permanentArenas))
            {
                int totalCreated = 0;

                foreach (string name in permanentArenas.Split(new char[] { ',', ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    ++totalCreated;
                    _logManager.LogM(LogLevel.Info, nameof(ArenaManager), $"Creating permanent arena '{name}'");
                    CreateArena(name, true);
                }

                _logManager.LogM(LogLevel.Info, nameof(ArenaManager), $"Created {totalCreated} permanent arena(s)");
            }

            return true;
        }

        bool IModuleLoaderAware.PreUnload(ComponentBroker broker)
        {
            //mm.ReleaseInterface<IPersist>();
            return true;
        }

        #endregion

        [ConfigHelp("Chat", "ForceFilter", ConfigScope.Global, typeof(bool), DefaultValue = "0",
            Description = "If true, players will always start with the obscenity filter on by default. If false, use their preference.")]
        private void Packet_GotoArena(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len != GoArenaPacket.LengthVIE && len != GoArenaPacket.LengthContinuum)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(ArenaManager), p, "bad arena packet len={0}", len);
                return;
            }

            GoArenaPacket go = new GoArenaPacket(data);

            if (go.ShipType > (byte)ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(ArenaManager), p, "bad shiptype in arena request");
                return;
            }

            // make a name from the request
            string name;
            int spx = 0;
            int spy = 0;
            if (go.ArenaType == -3)
            {
                if (!HasCapGo(p))
                    return;

                name = go.ArenaName;
            }
            else if (go.ArenaType == -2 || go.ArenaType == -1)
            {
                IArenaPlace ap = _broker.GetInterface<IArenaPlace>();
                if (ap != null)
                {
                    try
                    {
                        if (ap.Place(out name, ref spx, ref spy, p) == false)
                        {
                            name = "0";
                        }
                    }
                    finally
                    {
                        _broker.ReleaseInterface(ref ap);
                    }
                }
                else
                {
                    name = "0";
                }
            }
            else if (go.ArenaType >= 0)
            {
                if (!HasCapGo(p))
                    return;

                name = go.ArenaType.ToString();
            }
            else
            {
                _logManager.LogP(LogLevel.Malicious, nameof(ArenaManager), p, "bad arena type in arena request");
                return;
            }

            if (p.Type == ClientType.Continuum)
            {
                // TODO
                //IRedirect
            }

            CompleteGo(
                p,
                name,
                (ShipType)go.ShipType,
                go.XRes,
                go.YRes,
                (len >= GoArenaPacket.LengthContinuum) && go.OptionalGraphics != 0,
                go.WavMsg != 0,
                (go.ObscenityFilter != 0) || (_configManager.GetInt(_configManager.Global, "Chat", "ForceFilter", 0) != 0),
                spx,
                spy);
        }

        private void Packet_LeaveArena(Player p, byte[] data, int len)
        {
#if !CFG_RELAX_LENGTH_CHECKS
            if (len != 1)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(ArenaManager), p, "bad arena leaving packet len={0}", len);
            }
#endif
            LeaveArena(p);
        }

        private bool HasCapGo(Player p)
        {
            // TODO: 
            return true;
        }
    }
}

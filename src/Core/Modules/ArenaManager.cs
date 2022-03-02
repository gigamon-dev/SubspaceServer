using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that manages arenas, which includes the arena life-cycle: 
    /// the states they are in, transitions between states, movement of players between arenas, etc.
    /// </summary>
    [CoreModuleInfo]
    public class ArenaManager : IModule, IArenaManager, IArenaManagerInternal, IModuleLoaderAware
    {
        /// <summary>
        /// the read-write lock for the global arena list
        /// </summary>
        private readonly ReaderWriterLock _arenaLock = new();

        private readonly Dictionary<string, Arena> _arenaDictionary = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Key = module Type
        /// Value = list of arenas that have the module attached
        /// </summary>
        private readonly Dictionary<Type, List<Arena>> _attachedModules = new();

        internal ComponentBroker Broker;

        // required dependencies
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IMainloopTimer _mainloopTimer;
        private IModuleManager _moduleManager;
        private INetwork _network;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IServerTimer _serverTimer;

        // optional dependencies
        private IPersistExecutor _persistExecutor;

        private InterfaceRegistrationToken<IArenaManager> _iArenaManagerToken;
        private InterfaceRegistrationToken<IArenaManagerInternal> _iArenaManagerInternalToken;

        // for managing per arena data
        private readonly ReaderWriterLock _perArenaDataLock = new();
        private readonly SortedList<int, Type> _perArenaDataKeys = new();

        // population
        private int _playersTotal;
        private int _playersPlaying;
        private DateTime? _populationLastRefreshed;
        private readonly TimeSpan _populationRefreshThreshold = TimeSpan.FromMilliseconds(1000);
        private readonly object _populationRefreshLock = new();


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

        #region Locks

        private void ReadLock()
        {
            _arenaLock.AcquireReaderLock(Timeout.Infinite);
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

        #region IArenaManager and IArenaManagerInternal Members

        void IArenaManager.Lock()
        {
            ReadLock();
        }

        void IArenaManager.Unlock()
        {
            ReadUnlock();
        }

        Dictionary<string, Arena>.ValueCollection IArenaManager.ArenaList => _arenaDictionary.Values;

        void IArenaManagerInternal.SendArenaResponse(Player player)
        {
            if (player == null)
                return;

            Arena arena = player.Arena;

            if (arena == null)
            {
                _logManager.LogP(LogLevel.Warn, nameof(ArenaManager), player, "Bad arena in SendArenaResponse.");
                return;
            }

            _logManager.LogP(LogLevel.Info, nameof(ArenaManager), player, "Entering arena.");

            if (player.IsStandard)
            {
                // send whoami packet
                S2C_WhoAmI whoAmI = new((short)player.Id);
                _network.SendToOne(player, ref whoAmI, NetSendFlags.Reliable);
                
                // send settings
                IClientSettings clientset = Broker.GetInterface<IClientSettings>();
                if (clientset != null)
                {
                    try
                    {
                        clientset.SendClientSettings(player);
                    }
                    finally
                    {
                        Broker.ReleaseInterface(ref clientset);
                    }
                }
            }
            else if (player.IsChat)
            {
                // TODO: 
                //_chatnet.SendToOne(player, "INARENA:%s:%d", a.Name, player.Freq);
            }

            HashSet<Player> enterPlayerSet = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.Lock();
                try
                {
                    foreach (Player otherPlayer in _playerData.PlayerList)
                    {
                        if (otherPlayer.Status == PlayerState.Playing
                            && otherPlayer.Arena == arena
                            && otherPlayer != player)
                        {
                            // Add to the collection of players, we'll send later.
                            enterPlayerSet.Add(otherPlayer);
                            
                            // Tell others already in the arena, that the player is entering.
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
                    enterPlayerSet.Add(player); // include the player's own packet too

                    //
                    // Send all the player entering packets as one large packet.
                    //

                    int packetLength = enterPlayerSet.Count * S2C_PlayerData.Length;
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(packetLength);

                    try
                    {
                        Span<byte> bufferSpan = buffer.AsSpan(0, packetLength); // only the part we are going to use (Rent can return a larger array)

                        int index = 0;
                        S2C_PlayerDataBuilder builder = new(bufferSpan);
                        foreach (Player enteringPlayer in enterPlayerSet)
                        {
                            builder.Set(index++, ref enteringPlayer.Packet);
                        }

                        _network.SendToOne(player, bufferSpan, NetSendFlags.Reliable);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer, true);
                    }
                }
                else if (player.IsChat)
                {
                    foreach (Player enteringPlayer in enterPlayerSet)
                    {
                        SendEnter(enteringPlayer, player, true);
                    }
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(enterPlayerSet);
            }

            if (player.IsStandard)
            {
                IMapNewsDownload mapNewDownload = Broker.GetInterface<IMapNewsDownload>();
                if (mapNewDownload != null)
                {
                    try
                    {
                        mapNewDownload.SendMapFilename(player);
                    }
                    finally
                    {
                        Broker.ReleaseInterface(ref mapNewDownload);
                    }
                }

                Span<byte> span = stackalloc byte[1];

                // ASSS sends what it calls a "brick clear" packet here. Which is an empty, 1 byte brick packet (0x21).
                // However, there actually is no such mechanism to clear bricks on the client side. (would be nice to have though)
                // ASSS probably included it to emulate what subgame sends when there are no active bricks.
                // The Bricks module sends brick data on PlayerAction.EnterArena, which happens immediately after this method is called.
                //span[0] = (byte)S2CPacketType.Brick;
                //_network.SendToOne(player, span, NetSendFlags.Reliable);

                // send entering arena finisher
                span[0] = (byte)S2CPacketType.EnteringArena;
                _network.SendToOne(player, span, NetSendFlags.Reliable);

                if (player[_spawnkey] is SpawnLoc sp)
                {
                    if ((sp.X > 0) && (sp.Y > 0) && (sp.X < 1024) && (sp.Y < 1024))
                    {
                        S2C_WarpTo warpTo = new(sp.X, sp.Y);
                        _network.SendToOne(player, ref warpTo, NetSendFlags.Reliable);
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
                    _logManager.LogP(LogLevel.Error, nameof(ArenaManager), player, $"Player has an arena, but is in a bad state ({player.Status}).");
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
                            _logManager.LogA(LogLevel.Warn, nameof(ArenaManager), arena, "Can't recycle arena with fake players.");
                            return false;
                        }
                    }

                    S2C_WhoAmI whoAmI = new(0);

                    // first move playing players elsewhere
                    foreach (Player player in _playerData.PlayerList)
                    {
                        if (player.Arena == arena)
                        {
                            // send whoami packet so the clients leave the arena
                            if (player.IsStandard)
                            {
                                whoAmI.PlayerId = (short)player.Id;
                                _network.SendToOne(player, ref whoAmI, NetSendFlags.Reliable);
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
                finally
                {
                    _playerData.WriteUnlock();
                }

                // arena to close and then get resurrected
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
                        player.Packet.AcceptAudio != 0, 
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
                _logManager.LogP(LogLevel.Warn, nameof(ArenaManager), player, $"State sync problem: Sent arena request from bad status ({player.Status}).");
                return;
            }


            // remove all illegal characters and make lowercase
            string name;
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                sb.Append(reqName);
                for (int x = 0; x < sb.Length; x++)
                {
                    if (x == 0 && sb[x] == '#')
                        continue;
                    else if (!char.IsLetterOrDigit(sb[x]))
                        sb[x] = 'x';
                    else if (char.IsUpper(sb[x]))
                        sb[x] = char.ToLower(sb[x]);
                }

                if (sb.Length == 0)
                {
                    // this might occur when a player is redirected to us from another zone
                    IArenaPlace ap = Broker.GetInterface<IArenaPlace>();
                    if (ap != null)
                    {
                        try
                        {
                            int spx = 0, spy = 0;
                            if (!ap.Place(out name, ref spx, ref spy, player))
                            {
                                name = "0";
                            }
                        }
                        finally
                        {
                            Broker.ReleaseInterface(ref ap);
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
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
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
                            _logManager.LogM(LogLevel.Error, nameof(ArenaManager), "Internal error: No running arenas but cannot create new one.");
                            return;
                        }
                    }
                    else if (arena.Status > ArenaState.Running)
                    {
                        // arena is on it's way out
                        // this isn't a problem, just make sure that it will come back
                        if (arena[_adkey] is not ArenaData arenaData)
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
                player.Packet.AcceptAudio = voices ? (byte)1 : (byte)0;
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
                S2C_PlayerLeaving packet = new((short)player.Id);
                _network.SendToArena(arena, player, ref packet, NetSendFlags.Reliable);
                //chatnet.SendToArena(

                _logManager.LogP(LogLevel.Info, nameof(ArenaManager), player, "Leaving arena.");
            }
        }

        void IArenaManager.GetPopulationSummary(out int total, out int playing)
        {
            // Unless I'm missing something, thread synchronization in ASSS doesn't seem right.  
            // a read lock is being held for reading the arena list (supposed to be locked prior to calling this method)
            // a read lock is being held for reading the player list
            // but it's writing to each arena, meaning multiple threads could be writing to Arena.Total and Arena.Playing simultaneously
            // I've added a double checked lock, _populationRefreshLock, which will only allow 1 thread in to refresh the data at a given time.

            // TODO: Can ArenaManager/Arena be enhanced such that an increment/decrement occurs when players enter/leave, change ships, etc?

            if (RefreshNeeded())
            {
                lock (_populationRefreshLock)
                {
                    if (RefreshNeeded())
                    {
                        // refresh population stats
                        ICapabilityManager capman = Broker.GetInterface<ICapabilityManager>();

                        try
                        {
                            _playersTotal = _playersPlaying = 0;

                            foreach (Arena arena in _arenaDictionary.Values)
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
                                Broker.ReleaseInterface(ref capman);
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
                    if (_perArenaDataKeys.ContainsKey(key) == false)
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
                foreach (Arena arena in _arenaDictionary.Values)
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
                foreach (Arena arena in _arenaDictionary.Values)
                {
                    arena.RemoveExtraData(key);
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
                        if (arena[_adkey] is not ArenaData arenaData)
                            return;

                        arenaData.Holds++;
                        break;

                    default:
                        _logManager.LogA(LogLevel.Error, nameof(ArenaManager), arena, $"Hold called from invalid state ({arena.Status}).");
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
                        if (arena[_adkey] is not ArenaData arenaData)
                            return;

                        if (arenaData.Holds > 0)
                        {
                            arenaData.Holds--;
                        }
                        break;

                    default:
                        _logManager.LogA(LogLevel.Error, nameof(ArenaManager), arena, $"Unhold called from invalid state ({arena.Status}).");
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
                _moduleManager.AttachModule(moduleToAttach, a);
            }
        }

        private void ArenaConfChanged(Arena arena)
        {
            if (arena == null)
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
                _network.SendToOne(playerTo, ref player.Packet, NetSendFlags.Reliable);
            }
            else if (playerTo.IsChat)
            {
                //_chatNet.SendToOne(
            }
        }

        
        /// <summary>
        /// This is called when the persistent data retrieval or saving has completed.
        /// </summary>
        /// <param name="arena"></param>
        private void ArenaSyncDone(Arena arena)
        {
            WriteLock();

            try
            {
                if (arena.Status == ArenaState.WaitSync1)
                {
                    // persistent data has been retrieved from the database
                    arena.Status = ArenaState.Running;
                }
                else if (arena.Status == ArenaState.WaitSync2)
                {
                    // persistent data has been saved to the database
                    arena.Status = ArenaState.DoDestroy1;
                }
                else
                {
                    _logManager.LogA(LogLevel.Warn, nameof(ArenaManager), arena, $"ArenaSyncDone called from the wrong state ({arena.Status}).");
                }
            }
            finally
            {
                WriteUnlock();
            }
        }

        protected virtual void OnArenaAction(Arena arena, ArenaAction action)
        {
            if (arena != null)
                ArenaActionCallback.Fire(arena, arena, action);
            else
                ArenaActionCallback.Fire(Broker, arena, action);
        }

        private bool MainloopTimer_ProcessArenaStates()
        {
            WriteLock();
            try
            {
                foreach (Arena arena in _arenaDictionary.Values)
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
                            arena.SpecFreq = (short)_configManager.GetInt(arena.Cfg, "Team", "SpectatorFrequency", Arena.DefaultSpecFreq);
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
                            if (_persistExecutor != null)
                            {
                                arena.Status = ArenaState.WaitSync1;
                                _persistExecutor.GetArena(arena, ArenaSyncDone);
                            }
                            else
                            {
                                arena.Status = ArenaState.Running;
                            }
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
                                if (_persistExecutor != null)
                                {
                                    arena.Status = ArenaState.WaitSync2;
                                    _persistExecutor.PutArena(arena, ArenaSyncDone);
                                }
                                else
                                {
                                    arena.Status = ArenaState.DoDestroy1;
                                }
                            }
                            else
                            {
                                // oops, there is still at least one player still in the arena
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
                            if (_moduleManager.DetachAllFromArena(arena))
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
                                            arena.RemoveExtraData(kvp.Key);
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
                                _logManager.LogA(LogLevel.Error, nameof(ArenaManager), arena, "WARNING: The server is no longer in a stable state because of this error. your modules need to be fixed.");

                                // TODO: flush logs

                                arenaData.Resurrect = false;
                                arenaData.Reap = false;
                                arena.KeepAlive = true;
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
            Arena arena = new(Broker, name, this);
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

            _logManager.LogA(LogLevel.Info, nameof(ArenaManager), arena, "Created arena.");

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
                    foreach (Arena arena in _arenaDictionary.Values)
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

                    foreach (Arena arena in _arenaDictionary.Values)
                    {
                        ArenaData arenaData = arena[_adkey] as ArenaData;

                        if (arenaData.Reap && (arena.Status == ArenaState.Closing || !arena.KeepAlive))
                        {
                            _logManager.LogA(LogLevel.Drivel, nameof(ArenaManager), arena, 
                                $"Arena being {((arena.Status == ArenaState.Running) ? "destroyed" : "recycled")}.");

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
            IConfigManager configManager,
            ILogManager logManager,
            IMainloop mainloop,
            IMainloopTimer mainloopTimer,
            IModuleManager moduleManager,
            INetwork network,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IServerTimer serverTimer)
        {
            Broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _moduleManager = moduleManager ?? throw new ArgumentNullException(nameof(moduleManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _serverTimer = serverTimer ?? throw new ArgumentNullException(nameof(serverTimer));

            _spawnkey = _playerData.AllocatePlayerData<SpawnLoc>();

            IArenaManager amc = this;
            _adkey = amc.AllocateArenaData<ArenaData>();

            _network.AddPacket(C2SPacketType.GotoArena, Packet_GotoArena);
            _network.AddPacket(C2SPacketType.LeaveArena, Packet_LeaveArena);

            // TODO: 
            //_chatnet = Broker.GetInterface<IChatNet>();
            //if(_chatnet)
            //{
            //}

            _mainloopTimer.SetTimer(MainloopTimer_ProcessArenaStates, 100, 100, null);
            _mainloopTimer.SetTimer(ReapArenas, 1700, 1700, null);
            _mainloopTimer.SetTimer(MainloopTimer_DoArenaMaintenance, (int)TimeSpan.FromMinutes(10).TotalMilliseconds, (int)TimeSpan.FromMinutes(10).TotalMilliseconds, null);
            _serverTimer.SetTimer(ServerTimer_UpdateKnownArenas, 0, 1000, null);

            _iArenaManagerToken = Broker.RegisterInterface<IArenaManager>(this);
            _iArenaManagerInternalToken = Broker.RegisterInterface<IArenaManagerInternal>(this);

            return true;
        }

        private bool MainloopTimer_DoArenaMaintenance()
        {
            _arenaLock.AcquireWriterLock(Timeout.Infinite);

            try
            {
                foreach (Arena arena in _arenaDictionary.Values)
                {
                    arena.CleanupTeamTargets();
                }
            }
            finally
            {
                _arenaLock.ReleaseWriterLock();
            }

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iArenaManagerToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iArenaManagerInternalToken) != 0)
                return false;

            _network.RemovePacket(C2SPacketType.GotoArena, Packet_GotoArena);
            _network.RemovePacket(C2SPacketType.LeaveArena, Packet_LeaveArena);

            // TODO: 
            //if(_chatnet)
            //{
            //}

            _serverTimer.ClearTimer(ServerTimer_UpdateKnownArenas, null);
            _mainloopTimer.ClearTimer(MainloopTimer_ProcessArenaStates, null);
            _mainloopTimer.ClearTimer(ReapArenas, null);
            _mainloopTimer.ClearTimer(MainloopTimer_DoArenaMaintenance, null);

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
            _persistExecutor = broker.GetInterface<IPersistExecutor>();

            string permanentArenas = _configManager.GetStr(_configManager.Global, "Arenas", "PermanentArenas");
            if (!string.IsNullOrWhiteSpace(permanentArenas))
            {
                int totalCreated = 0;

                foreach (string name in permanentArenas.Split(new char[] { ',', ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    ++totalCreated;
                    _logManager.LogM(LogLevel.Info, nameof(ArenaManager), $"Creating permanent arena '{name}'.");
                    CreateArena(name, true);
                }

                _logManager.LogM(LogLevel.Info, nameof(ArenaManager), $"Created {totalCreated} permanent arena(s).");
            }

            return true;
        }

        bool IModuleLoaderAware.PreUnload(ComponentBroker broker)
        {
            if (_persistExecutor != null)
            {
                broker.ReleaseInterface(ref _persistExecutor);
            }

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

            if (len != C2S_GoArena.LengthVIE && len != C2S_GoArena.LengthContinuum)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(ArenaManager), p, $"Bad arena packet len={len}.");
                return;
            }

            ref C2S_GoArena go = ref MemoryMarshal.AsRef<C2S_GoArena>(data);

            if (go.ShipType > (byte)ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(ArenaManager), p, "Bad shiptype in arena request.");
                return;
            }

            // make a name from the request
            string name;
            int spx = 0;
            int spy = 0;

            if (go.ArenaType == -3) // private arena
            {
                if (!HasCapGo(p))
                    return;

                name = go.ArenaName;
            }
            else if (go.ArenaType == -2 || go.ArenaType == -1) // any public arena (server chooses)
            {
                IArenaPlace ap = Broker.GetInterface<IArenaPlace>();

                if (ap != null)
                {
                    try
                    {
                        if (!ap.Place(out name, ref spx, ref spy, p))
                        {
                            name = "0";
                        }
                    }
                    finally
                    {
                        Broker.ReleaseInterface(ref ap);
                    }
                }
                else
                {
                    name = "0";
                }
            }
            else if (go.ArenaType >= 0) // specific public arena
            {
                if (!HasCapGo(p))
                    return;

                name = go.ArenaType.ToString();
            }
            else
            {
                _logManager.LogP(LogLevel.Malicious, nameof(ArenaManager), p, "Bad arena type in arena request.");
                return;
            }

            if (p.Type == ClientType.Continuum)
            {
                // TODO: ability to redirect to another server/zone
                //IRedirect
            }

            CompleteGo(
                p,
                name,
                (ShipType)go.ShipType,
                go.XRes,
                go.YRes,
                (len >= C2S_GoArena.LengthContinuum) && go.OptionalGraphics != 0,
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
                _logManager.LogP(LogLevel.Malicious, nameof(ArenaManager), p, $"Bad arena leaving packet len={len}.");
            }
#endif
            LeaveArena(p);
        }

        private bool HasCapGo(Player p)
        {
            if (p == null)
                return false;

            ICapabilityManager capabilityManager = Broker.GetInterface<ICapabilityManager>();

            try
            {
                return capabilityManager == null || capabilityManager.HasCapability(p, "cmd_go");
            }
            finally
            {
                if (capabilityManager != null)
                {
                    Broker.ReleaseInterface(ref capabilityManager);
                }
            }
        }
    }
}

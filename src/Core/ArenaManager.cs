using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using SS.Core.Packets;

namespace SS.Core
{
    public delegate void ArenaActionEventHandler(Arena arena, ArenaAction action);

    internal interface IArenaManagerCore
    {
        void SendArenaResponse(Player player);
        void LeaveArena(Player player);
    }

    public class ArenaManager : IModule, IArenaManagerCore
    {
        /// <summary>
        /// the read-write lock for the global arena list
        /// </summary>
        private ReaderWriterLock _arenaLock = new ReaderWriterLock();

        private Dictionary<string, Arena> _arenaDictionary = new Dictionary<string, Arena>();

        /// <summary>
        /// Key = module Type
        /// Value = list of arenas that have the module attached
        /// </summary>
        private Dictionary<Type, List<Arena>> _attachedModules = new Dictionary<Type, List<Arena>>();


        private ModuleManager _mm;
        
        // other modules
        private ILogManager _logManager;
        private IPlayerData _pd;
        private IConfigManager _cfg;
        private IServerTimer _ml;

        // for managing per player data
        private SortedList<int, Type> _perArenaDataKeys = new SortedList<int, Type>();

        // per arena data key (stores the resurrect boolean flag)
        private int _adkey;

        public event ArenaActionEventHandler ArenaActionEvent;

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

        // per player data key (stores SpawnLoc)
        private int _spawnkey;

        public ArenaManager()
        {
        }

        /// <summary>
        /// remember to use Lock/Unlock
        /// </summary>
        public IEnumerable<Arena> ArenaList
        {
            get
            {
                return _arenaDictionary.Values;
            }
        }

        #region Locks

        /// <summary>
        /// Locks the global arena lock.
        /// There is a lock protecting the arena list, which you need to hold
        /// whenever you access ArenaList. 
        /// Call this before you start, and Unlock() when you're done.
        /// </summary>
        public void Lock()
        {
            _arenaLock.AcquireReaderLock(0);
        }

        /// <summary>
        /// Unlocks the global arena lock.
        /// Use this whenever you used Lock()
        /// </summary>
        public void Unlock()
        {
            _arenaLock.ReleaseReaderLock();
        }

        private void writeLock()
        {
            _arenaLock.AcquireWriterLock(0);
        }

        private void writeUnlock()
        {
            _arenaLock.ReleaseWriterLock();
        }

        #endregion

        #region IArenaManagerCore Members

        void IArenaManagerCore.SendArenaResponse(Player player)
        {
            SimplePacket whoami = new SimplePacket();
            whoami.type = (byte)S2CPacketType.WhoAmI;

            Arena arena = player.Arena;

            if (arena == null)
            {
                Console.WriteLine("<arenaman> *warning* bad arena in SendArenaResponse");
                return;
            }

            Console.WriteLine("<arenaman> {0} {1} entering arena", player.Name, arena.Name);

            if (player.IsStandard)
            {
                // send whoami packet
                whoami.d1 = (short)player.Id;
                //_net.SendToOne(player, whoami.GetBytes(), 3, NetworkSendFlag.NetReliable);
                /*
                // send settings
                ClientSet clientset = _mm.GetModule<ClientSet>();
                if (clientset != null)
                {
                    clientset.SendClientSettings(player);
                }
                */
            }
            else if (player.IsChat)
            {
                // TODO: 
                //_chatnet.SendToOne(player, "INARENA:%s:%d", a.Name, player.Freq);
            }

            _pd.Lock();
            foreach (Player otherPlayer in _pd.PlayerList)
            {
                if (otherPlayer.Status == PlayerState.Playing &&
                    otherPlayer.Arena == arena &&
                    otherPlayer != player)
                {
                    // send each other info
                    sendEnter(otherPlayer, player, true);
                    sendEnter(player, otherPlayer, false);
                }
            }
            _pd.Unlock();

            if (player.IsStandard)
            {
                /*
                MapNewsDl map = _mm.GetModule<MapNewsDl>();
                if (map != null)
                {
                    map.SendMapFilename(player);
                }
                */

                // send brick clear and finisher
                //whoami.type = S2CPacketType.Brick;
                //_net.SendToOne(player, whoami.GetBytes(), 1, NetworkSendFlag.NetReliable);

                //whoami.type = S2CPacketType.EnteringArena;
                //_net.SendToOne(player, whoami.GetBytes(), 1, NetworkSendFlag.NetReliable);

                SpawnLoc sp = player[_spawnkey] as SpawnLoc;

                if(sp != null)
                {
                    if ((sp.X > 0) && (sp.Y > 0) && (sp.X < 1024) && (sp.Y < 1024))
                    {
                        SimplePacket wto = new SimplePacket();
                        wto.type = (byte)S2CPacketType.WarpTo;
                        wto.d1 = sp.X;
                        wto.d2 = sp.Y;
                        //_net.SendToOne(player, wto, 5, NetworkSendFlag.NetReliable);
                    }
                }
            }
        }

        void IArenaManagerCore.LeaveArena(Player player)
        {
            bool notify;

            try
            {
                _pd.WriteLock();

                Arena arena = player.Arena;
                if (arena == null)
                    return;

                notify = initiateLeaveArena(player);
            }
            finally
            {
                _pd.WriteUnlock();
            }

            if (notify)
            {
                SimplePacket pk = new SimplePacket();
                pk.type = (byte)S2CPacketType.PlayerLeaving;
                pk.d1 = (short)player.Id;

                //_net.SendToArena(
                //chatnet.SendToArena(
                //lm.Log(
            }
        }

        private bool initiateLeaveArena(Player player)
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
                    player.flags.leave_arena_when_done_waiting = true;
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

        public int RecycleArena(Arena arena)
        {
            // TODO:
            return -1;
        }

        public void SendToArena(Player p, string arenaName, int spawnx, int spawny)
        {
            // TODO:
        }

        public Arena FindArena(string name)
        {
            try
            {
                Lock();

                return doFindArena(name, ArenaState.Running, ArenaState.Running);
            }
            finally
            {
                Unlock();
            }
        }
        public Arena FindArena(string name, out int totalCount, out int playing)
        {
            Arena arena = FindArena(name);

            if (arena != null)
            {
                countPlayers(arena, out totalCount, out playing);
            }
            else
            {
                totalCount = 0;
                playing = 0;
            }

            return arena;
        }

        private Arena doFindArena(string name, ArenaState minState, ArenaState maxState)
        {
            try
            {
                Lock();

                Arena arena;
                if (_arenaDictionary.TryGetValue(name, out arena) == false)
                    return null;

                if (arena.Status >= minState && arena.Status <= maxState)
                    return arena;
                
                /*
                foreach (Arena arena in ArenaList)
                {
                    if (arena.Status >= minState &&
                        arena.Status <= maxState &&
                        string.Compare(arena.Name, name) == 0)
                    {
                        return arena;
                    }
                }
                */
                return null;
            }
            finally
            {
                Unlock();
            }
        }

        private void countPlayers(Arena arena, out int total, out int playing)
        {
            int totalCount = 0;
            int playingCount = 0;

            try
            {
                _pd.Lock();

                foreach (Player player in _pd.PlayerList)
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
                _pd.Unlock();
            }

            total = totalCount;
            playing = playingCount;
        }

        public void GetPopulationSummary(out int total, out int playing)
        {
            // TODO: 
            total = 0;
            playing = 0;
        }

        public int AllocateArenaData<T>() where T : new()
        {
            int key = 0;

            lock (_perArenaDataKeys)
            {
                // find next available key
                for (key = 0; key < _perArenaDataKeys.Keys.Count; key++)
                {
                    if (_perArenaDataKeys.Keys.Contains(key) == false)
                        break;
                }

                _perArenaDataKeys[key] = typeof(T);
            }

            Lock();

            foreach (Arena arena in ArenaList)
            {
                arena[key] = new T();
            }

            Unlock();

            return key;
        }

        public void FreeArenaData(int key)
        {
            lock (_perArenaDataKeys)
            {
                _perArenaDataKeys.Remove(key);

                Lock();
                foreach (Arena arena in ArenaList)
                {
                    arena.RemovePerArenaData(key);
                }
                Unlock();
            }
        }

        public void AttachModule(IModule moduleToAttach, Arena arenaToAttachTo)
        {
            // TODO: implement this, and maybe move to Arena class?
        }

        public void DetachModule(IModule moduleToDetach, Arena arenaToDetachFrom)
        {
            // TODO: implement this, and maybe move to Arena class?
        }

        public void DetachAllFromArena(Arena arenaToDetachFrom)
        {
            // TODO: implement this, and maybe move to Arena class?
        }

        private void doAttach(Arena a)
        {
            string attachMods = _cfg.GetStr(a.Cfg, "Modules", "AttachModules");
            if (attachMods == null)
                return;

            string[] attachModsArray = attachMods.Split(" \t:;,".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);

            foreach (string moduleToAttach in attachModsArray)
            {
                //_mm.AttachModule(moduleToAttach, a);
            }
        }

        private void arenaConfChanged(object clos)
        {
            Arena arena = clos as Arena;
            if (arena == null)
                return;

            try
            {
                Lock();

                if (arena.Status == ArenaState.Running)
                {
                    if (ArenaActionEvent != null)
                    {
                        ArenaActionEvent(arena, ArenaAction.ConfChanged);
                    }
                }
            }
            finally
            {
                Unlock();
            }
        }

        private void sendEnter(Player player, Player playerTo, bool already)
        {
            if (playerTo.IsStandard)
            {
                //net.SendToOne(
            }
            else if (playerTo.IsChat)
            {
                //chatnet.SendToOne(
            }
        }

        private void arenaSyncDone(Arena arena)
        {
            try
            {
                writeLock();

                switch(arena.Status)
                {
                    case ArenaState.WaitSync1:
                        arena.Status = ArenaState.Running;
                        break;

                    case ArenaState.WaitSync2:
                        arena.Status = ArenaState.DoDeinit;
                        break;

                    default:
                        // TODO: when log manager is available
                        //lm->LogA(L_WARN, "arenaman", a, "arena_sync_done called from wrong state");
                        break;
                }
            }
            finally
            {
                writeUnlock();
            }
        }

        private bool processArenaStates(object dummy)
        {
            try
            {
                writeLock();

                foreach (Arena arena in ArenaList)
                {
                    ArenaState status = arena.Status;
                    
                    switch (status)
                    {
                        case ArenaState.Running:
                        case ArenaState.Closing:
                        case ArenaState.WaitSync1:
                        case ArenaState.WaitSync2:
                            continue;
                    }

                    switch (status)
                    {
                        case ArenaState.DoInit:
                            if (ArenaActionEvent != null)
                            {
                                ArenaActionEvent(arena, ArenaAction.PreCreate);
                            }

                            arena.Cfg = _cfg.OpenConfigFile(arena.BaseName, null, new ConfigChangedDelegate(arenaConfChanged), arena);
                            arena.SpecFreq = _cfg.GetInt(arena.Cfg, "Team", "SpectatorFrequency", Arena.DEFAULT_SPEC_FREQ);
                            doAttach(arena);

                            if (ArenaActionEvent != null)
                            {
                                ArenaActionEvent(arena, ArenaAction.Create);
                            }

                            /*
                            if (persist != null)
                            {
                            }
                            else
                            */

                            arena.Status = ArenaState.Running;

                            break;

                        case ArenaState.DoWriteData:
                            bool hasPlayers = false;
                            try
                            {
                                _pd.Lock();
                                foreach (Player p in _pd.PlayerList)
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
                                _pd.Unlock();
                            }

                            if (hasPlayers == false)
                            {
                                /*
                                if (persist != null)
                                {
                                    persist.PutArena(arena, arenaSyncDone);
                                    arena.Status = ArenaState.WaitSync2;
                                }
                                else
                                {
                                */
                                    arena.Status = ArenaState.DoDeinit;
                                //}
                            }
                            else
                            {
                                arena.Status = ArenaState.Running;
                            }
                            break;

                        case ArenaState.DoDeinit:
                            if (ArenaActionEvent != null)
                            {
                                ArenaActionEvent(arena, ArenaAction.Destroy);
                            }

                            //mm->DetachAllFromArena(arena);

                            _cfg.CloseConfigFile(arena.Cfg);

                            if (ArenaActionEvent != null)
                            {
                                ArenaActionEvent(arena, ArenaAction.PostDestroy);
                            }

                            if ((bool)arena[_adkey])
                            {
                                // clear all private data on recycle, so it looks to modules like it was just created.
                                foreach (int perArenaDataKey in _perArenaDataKeys.Keys)
                                {
                                    arena[perArenaDataKey] = null;
                                }
                                arena[_adkey] = false;
                                arena.Status = ArenaState.DoInit;
                            }
                            else
                            {
                                _arenaDictionary.Remove(arena.Name);
                            }

                            break;
                    }
                }
            }
            finally
            {
                writeUnlock();
            }

            return true;
        }

        private bool reapArenas(object dummy)
        {
            try
            {
                Lock();
                _pd.Lock();

                foreach (Arena arena in ArenaList)
                {
                    if (arena.Status == ArenaState.Running || arena.Status == ArenaState.Closing)
                    {
                        bool skip = false;
                        bool resurrect = false;

                        foreach (Player player in _pd.PlayerList)
                        {
                            // if any player is currently using this arena, it can't
                            // be reaped. also, if anyone is entering a running
                            //arena, don't reap that.
                            if(player.Arena == arena ||
                                player.NewArena == arena && arena.Status == ArenaState.Running)
                            {
                                skip = true;
                            }

                            // we do allow reaping of a closing arena that someone
                            // wants to re-enter, but we first make sure that it
                            // will reappear.
                            if (player.NewArena == arena)
                            {
                                resurrect = true;
                            }
                        }

                        if (skip)
                            continue;

                        if(resurrect)
                            arena[_adkey] = true; // resurrect = true

                        Console.WriteLine("<arenaman> {" + arena.Name + "} arena being " + 
                            ((arena.Status == ArenaState.Running) ? "destroyed" : "recycled"));

                        // set its status so that the arena processor will do appropriate things
                        arena.Status = ArenaState.DoWriteData;
                    }
                }
            }
            finally
            {
                _pd.Unlock();
                Unlock();
            }

            return true;
        }

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get
            {
                return new Type[] {
                    typeof(ILogManager), 
                    typeof(IPlayerData), 
                    typeof(IConfigManager), 
                    typeof(IServerTimer)
                };
            }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IModuleInterface> interfaceDependencies)
        {
            _mm = mm;
            _mm.ModuleUnloading += _mm_ModuleUnloading;

            _logManager = interfaceDependencies[typeof(ILogManager)] as ILogManager;
            _pd = interfaceDependencies[typeof(IPlayerData)] as IPlayerData;
            //_net = interfaceDependencies[typeof(Network)] as INetwork;
            //_chatnet = 
            _cfg = interfaceDependencies[typeof(IConfigManager)] as IConfigManager;
            _ml = interfaceDependencies[typeof(IServerTimer)] as IServerTimer;

            if (_pd == null || _cfg == null || _ml == null)
                return false;

            _spawnkey = _pd.AllocatePlayerData<SpawnLoc>();

            _adkey = AllocateArenaData<bool>();

            _ml.SetTimer<object>(new TimerDelegate<object>(processArenaStates), 20, 20, null, null);
            _ml.SetTimer<object>(new TimerDelegate<object>(reapArenas), 170, 170, null, null);

            _logManager.Log(LogLevel.Drivel, "ArenaManager.Load");
            _logManager.LogP(LogLevel.Warn, "ArenaManager", null, "testing 123");
            return true;
        }

        private void _mm_ModuleUnloading(object sender, ModuleUnloadingEventArgs e)
        {
            // TODO: handle unattaching the module from whatever arenas it is attached to
            // remember to do this in a threadsafe manner..
            /*
            foreach (Arena arena in _arenaDictionary.Values)
            {
                
            }
            */
        }

        bool IModule.Unload(ModuleManager mm)
        {
            _mm.ModuleUnloading -= _mm_ModuleUnloading;

            _ml.ClearTimer<object>(new TimerDelegate<object>(processArenaStates), null);
            _ml.ClearTimer<object>(new TimerDelegate<object>(reapArenas), null);

            FreeArenaData(_adkey);

            _arenaDictionary.Clear();

            _pd.FreePlayerData(_spawnkey);

            FreeArenaData(_adkey);

            return true;
        }

        #endregion
    }
}

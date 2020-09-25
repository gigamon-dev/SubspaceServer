using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using SS.Utilities;
using SS.Core.Map;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class Game : IModule, IGame
    {
        private ComponentBroker _broker;
        private IPlayerData _playerData;
        private IConfigManager _configManager;
        private IMainloop _mainloop;
        private ILogManager _logManager;
        private INetwork _net;
        //private IChatNet _chatnet;
        private IArenaManager _arenaManager;
        private ICapabilityManager _capabilityManager;
        private IMapData _mapData;
        private ILagCollect _lagCollect;
        private IChat _chat;
        private ICommandManager _commandManager;
        //private IPersist _persist;
        private InterfaceRegistrationToken _iGameToken;

        private int _pdkey;
        private int _adkey;

        private readonly object _specmtx = new object();
        private readonly object _freqshipmtx = new object();

        private const int WeaponCount = 32;

        private static readonly byte[] _addSpecBytes = new byte[] { (byte)S2CPacketType.SpecData, 1 };
        private static readonly byte[] _clearSpecBytes = new byte[] { (byte)S2CPacketType.SpecData, 0 };
        private static readonly byte[] _shipResetBytes = new byte[] { (byte)S2CPacketType.ShipReset };

        private readonly Random random = new Random();

        [Flags]
        private enum PersonalGreen
        {
            None = 0, 
            Thor = 1, 
            Burst = 2, 
            Brick = 4, 
        }

        private class PlayerData
        {
            public C2SPositionPacket pos = new C2SPositionPacket(new byte[C2SPositionPacket.LengthWithExtra]);

            /// <summary>
            /// who the player is spectating, null means not spectating
            /// </summary>
            public Player speccing;

            /// <summary>
            /// # of weapon packets
            /// </summary>
            public uint wpnSent;

            /// <summary>
            /// used for determining which weapon packets to ignore for the player, if any
            /// e.g. if the player is lagging badly, it can be set to handicap against the player
            /// </summary>
            public int ignoreWeapons;

            /// <summary>
            /// # of deaths the player has had without firing, used to check if the player should be sent to spec
            /// </summary>
            public int deathWithoutFiring;

            // extra position data/energy stuff
            public int epdQueries;

            public struct PlExtraPositionData
            {
                /// <summary>
                /// whose energy levels the player can see
                /// </summary>
                public SeeEnergy seeNrg;

                /// <summary>
                /// whose energy levels the player can see when in spectator mode
                /// </summary>
                public SeeEnergy seeNrgSpec;

                /// <summary>
                /// whether the player can see extra position data
                /// </summary>
                public bool seeEpd;
            }
            public PlExtraPositionData pl_epd;

            // some flags
            public bool lockship;

            /// <summary>
            /// Whether the player is in a <see cref="MapRegion"/> that does not allow antiwarp.
            /// </summary>
            public bool MapRegionNoAnti;

            /// <summary>
            /// Whether the player is in a <see cref="MapRegion"/> that does not allow firing of weapons.
            /// </summary>
            public bool MapRegionNoWeapons;

            /// <summary>
            /// when the lock expires, or null for session-long lock
            /// </summary>
            public DateTime? expires;

            /// <summary>
            /// when we last updated the region-based flags
            /// </summary>
            public DateTime lastRgnCheck;

            public Dictionary<MapRegion, bool> LastRegionSet = new Dictionary<MapRegion, bool>();
            //public LinkedList<object> lastrgnset = new LinkedList<object>();

            public ShipType PlayerPostitionPacket_LastShip;
        }

        private class ArenaData
        {
            /// <summary>
            /// whether spectators in the arena can see extra data for the person they're spectating
            /// </summary>
            public bool SpecSeeExtra;

            /// <summary>
            /// whose energy levels spectators can see
            /// </summary>
            public SeeEnergy SpecSeeEnergy;

            /// <summary>
            /// whose energy levels everyone can see
            /// </summary>
            public SeeEnergy AllSeeEnergy;

            public PersonalGreen personalGreen;
            public bool initLockship;
            public bool initSpec;
            public int MaxDeathWithoutFiring;
            public TimeSpan RegionCheckTime;
            public bool NoSafeAntiwarp;
            public int WarpThresholdDelta;
            public int cfg_pospix;
            public int cfg_sendanti;
            public int cfg_AntiwarpRange;
            public int cfg_EnterDelay;
            public int[] wpnRange = new int[WeaponCount];
        }

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IPlayerData playerData,
            IConfigManager configManager,
            IMainloop mainloop,
            ILogManager logManager,
            INetwork net,
            //IChatNet chatnet,
            IArenaManager arenaManager,
            ICapabilityManager capabilityManager,
            IMapData mapData,
            ILagCollect lagCollect,
            IChat chat,
            ICommandManager commandManager)
            //IPersist persist)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _net = net ?? throw new ArgumentNullException(nameof(net));
            //_chatnet = chatnet ?? throw new ArgumentNullException(nameof(chatnet));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _lagCollect = lagCollect ?? throw new ArgumentNullException(nameof(lagCollect));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            //_persist = persist ?? throw new ArgumentNullException(nameof(persist));

            _adkey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdkey = _playerData.AllocatePlayerData<PlayerData>();
            
            //if(_persist != null)
                //_persist.

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            PlayerActionCallback.Register(_broker, Callback_PlayerAction);
            NewPlayerCallback.Register(_broker, Callback_NewPlayer);

            _net.AddPacket((int)C2SPacketType.Position, new PacketDelegate(Packet_Position));
            _net.AddPacket((int)C2SPacketType.SpecRequest, new PacketDelegate(Packet_SpecRequest));
            _net.AddPacket((int)C2SPacketType.SetShip, new PacketDelegate(Packet_SetShip));
            _net.AddPacket((int)C2SPacketType.SetFreq, new PacketDelegate(Packet_SetFreq));
            _net.AddPacket((int)C2SPacketType.Die, new PacketDelegate(Packet_Die));
            _net.AddPacket((int)C2SPacketType.Green, new PacketDelegate(Packet_Green));
            _net.AddPacket((int)C2SPacketType.AttachTo, new PacketDelegate(Packet_AttachTo));
            _net.AddPacket((int)C2SPacketType.TurretKickOff, new PacketDelegate(Packet_TurretKickoff));

            //if(_chatnet != null)
                //_chatnet.

            if (_commandManager != null)
            {
                _commandManager.AddCommand("spec", Command_spec);
                _commandManager.AddCommand("energy", Command_energy);
            }

            _iGameToken = _broker.RegisterInterface<IGame>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<IGame>(ref _iGameToken) != 0)
                return false;

            //if(_chatnet != null)

            if (_commandManager != null)
            {
                _commandManager.RemoveCommand("spec", Command_spec, null);
                _commandManager.RemoveCommand("energy", Command_energy, null);
            }

            _net.RemovePacket((int)C2SPacketType.Position, new PacketDelegate(Packet_Position));
            _net.RemovePacket((int)C2SPacketType.SpecRequest, new PacketDelegate(Packet_SpecRequest));
            _net.RemovePacket((int)C2SPacketType.SetShip, new PacketDelegate(Packet_SetShip));
            _net.RemovePacket((int)C2SPacketType.SetFreq, new PacketDelegate(Packet_SetFreq));
            _net.RemovePacket((int)C2SPacketType.Die, new PacketDelegate(Packet_Die));
            _net.RemovePacket((int)C2SPacketType.Green, new PacketDelegate(Packet_Green));
            _net.RemovePacket((int)C2SPacketType.AttachTo, new PacketDelegate(Packet_AttachTo));
            _net.RemovePacket((int)C2SPacketType.TurretKickOff, new PacketDelegate(Packet_TurretKickoff));

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);
            NewPlayerCallback.Unregister(_broker, Callback_NewPlayer);

            _mainloop.WaitForMainWorkItemDrain();

            //if(_persist != null)

            _arenaManager.FreeArenaData(_adkey);
            _playerData.FreePlayerData(_pdkey);

            return true;
        }

        #endregion

        #region IGame Members

        void IGame.SetFreq(Player p, short freq)
        {
            if (p == null)
                return;

            SetFreq(p, freq);
        }

        void IGame.SetShip(Player p, ShipType ship)
        {
            if (p == null)
                return;

            SetShipAndFreq(p, ship, p.Freq);
        }

        void IGame.SetShipAndFreq(Player p, ShipType ship, short freq)
        {
            SetShipAndFreq(p, ship, freq);
        }

        void IGame.WarpTo(ITarget target, short x, short y)
        {
            if(target == null)
                throw new ArgumentNullException("target");

            using (DataBuffer buffer = Pool<DataBuffer>.Default.Get())
            {
                SimplePacket sp = new SimplePacket(buffer.Bytes);
                sp.Type = (byte)S2CPacketType.WarpTo;
                sp.D1 = x;
                sp.D2 = y;

                _net.SendToTarget(target, buffer.Bytes, 5, NetSendFlags.Reliable | NetSendFlags.Urgent);
            }
        }

        void IGame.GivePrize(ITarget target, Prize prizeType, short count)
        {
            if(target == null)
                throw new ArgumentNullException("target");

            using (DataBuffer buffer = Pool<DataBuffer>.Default.Get())
            {
                SimplePacket sp = new SimplePacket(buffer.Bytes);
                sp.Type = (byte)S2CPacketType.PrizeRecv;
                sp.D1 = count;
                sp.D2 = (short)prizeType;

                _net.SendToTarget(target, buffer.Bytes, 5, NetSendFlags.Reliable);
            }
        }

        void IGame.Lock(ITarget target, bool notify, bool spec, int timeout)
        {
            LockWork(target, true, notify, spec, timeout);
        }

        void IGame.Unlock(ITarget target, bool notify)
        {
            LockWork(target, false, notify, false, 0);
        }

        bool IGame.HasLock(Player p)
        {
            if (p == null || !(p[_pdkey] is PlayerData pd))
                return false;

            return pd.lockship;
        }

        void IGame.LockArena(Arena arena, bool notify, bool onlyArenaState, bool initial, bool spec)
        {
            if (!(arena[_adkey] is ArenaData ad))
                return;

            ad.initLockship = true;
            if (!initial)
                ad.initSpec = true;

            if (!onlyArenaState)
            {
                LockWork(Target.ArenaTarget(arena), true, notify, spec, 0);
            }
        }

        void IGame.UnlockArena(Arena arena, bool notify, bool onlyArenaState)
        {
            if (!(arena[_adkey] is ArenaData ad))
                return;

            ad.initLockship = false;
            ad.initSpec = false;

            if (!onlyArenaState)
            {
                LockWork(Target.ArenaTarget(arena), false, notify, false, 0);
            }
        }

        bool IGame.HasLock(Arena arena)
        {
            if (arena == null || !(arena[_adkey] is ArenaData ad))
                return false;

            return ad.initLockship;
        }

        void IGame.FakePosition(Player p, C2SPositionPacket pos, int len)
        {
            HandlePositionPacket(p, pos, len, true);
        }

        void IGame.FakeKill(Player killer, Player killed, short pts, short flags)
        {
            NotifyKill(killer, killed, pts, flags, 0);
        }

        float IGame.GetIgnoreWeapons(Player p)
        {
            if (p == null)
                return 0;

            if (!(p[_pdkey] is PlayerData pd))
                return 0;

            return pd.ignoreWeapons / (float)Constants.RandMax;
        }

        void IGame.SetIgnoreWeapons(Player p, float proportion)
        {
            if (p == null)
                return;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            pd.ignoreWeapons = (int)((float)Constants.RandMax * proportion);
        }

        void IGame.ShipReset(ITarget target)
        {
            if (target == null)
                throw new ArgumentNullException("target");

            _net.SendToTarget(target, _shipResetBytes, _shipResetBytes.Length, NetSendFlags.Reliable);

            _playerData.Lock();

            try
            {

                _playerData.TargetToSet(target, out LinkedList<Player> list);

                foreach (Player p in list)
                {
                    if (p.Ship == ShipType.Spec)
                        continue;

                    SpawnCallback.SpawnReason flags = SpawnCallback.SpawnReason.ShipReset;
                    if (p.Flags.IsDead)
                    {
                        p.Flags.IsDead = false;
                        flags |= SpawnCallback.SpawnReason.AfterDeath;
                    }

                    DoSpawnCallback(p, flags);
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        void IGame.IncrementWeaponPacketCount(Player p, int packets)
        {
            if (p == null)
                return;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            pd.wpnSent = (uint)(pd.wpnSent + packets);
        }

        void IGame.SetPlayerEnergyViewing(Player p, SeeEnergy value)
        {
            if (p == null)
                return;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            pd.pl_epd.seeNrg = value;
        }

        void IGame.SetSpectatorEnergyViewing(Player p, SeeEnergy value)
        {
            if (p == null)
                return;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            pd.pl_epd.seeNrgSpec = value;
        }

        void IGame.ResetPlayerEnergyViewing(Player p)
        {
            if (p == null)
                return;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            if (!(p.Arena[_adkey] is ArenaData ad))
                return;

            SeeEnergy seeNrg = SeeEnergy.None;
            if (ad.AllSeeEnergy != SeeEnergy.None)
                seeNrg = ad.AllSeeEnergy;

            if (_capabilityManager != null &&
                _capabilityManager.HasCapability(p, Constants.Capabilities.SeeEnergy))
            {
                seeNrg = SeeEnergy.All;
            }

            pd.pl_epd.seeNrg = seeNrg;
        }

        void IGame.ResetSpectatorEnergyViewing(Player p)
        {
            if (p == null)
                return;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            if (!(p.Arena[_adkey] is ArenaData ad))
                return;

            SeeEnergy seeNrgSpec = SeeEnergy.None;
            if (ad.SpecSeeEnergy != SeeEnergy.None)
                seeNrgSpec = ad.SpecSeeEnergy;

            if (_capabilityManager != null &&
                _capabilityManager.HasCapability(p, Constants.Capabilities.SeeEnergy))
            {
                seeNrgSpec = SeeEnergy.All;
            }

            pd.pl_epd.seeNrgSpec = seeNrgSpec;
        }

        bool IGame.IsAntiwarped(Player p, LinkedList<Player> playersList)
        {
            if (p == null || !(p[_pdkey] is PlayerData pd))
                return false;

            if (!(p.Arena[_adkey] is ArenaData ad))
                return false;

            if (pd.MapRegionNoAnti)
                return false;

            bool antiwarped = false;
            
            _playerData.Lock();

            try
            {
                foreach (Player i in _playerData.PlayerList)
                {
                    if (p == null || !(p[_pdkey] is PlayerData iData))
                        continue;

                    if (i.Arena == p.Arena
                        && i.Freq != p.Freq
                        && i.Ship != ShipType.Spec
                        && (i.Position.Status & PlayerPositionStatus.Antiwarp) != 0
                        && !iData.MapRegionNoAnti
                        && ((i.Position.Status & PlayerPositionStatus.Safezone) != 0 || !ad.NoSafeAntiwarp))
                    {
                        int dx = i.Position.X - p.Position.X;
                        int dy = i.Position.Y - p.Position.Y;
                        int distSquared = dx * dx + dy * dy;

                        if (distSquared < ad.cfg_AntiwarpRange)
                        {
                            antiwarped = true;

                            if (playersList != null)
                            {
                                playersList.AddLast(i);
                            }
                            else
                            {
                                // we found one, but no list to populate, so we're done
                                break;
                            }
                        }
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            return antiwarped;
        }

        #endregion

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null)
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ArenaData ad = arena[_adkey] as ArenaData;

                // How often to check for region enter/exit events (in ticks).
                ad.RegionCheckTime = TimeSpan.FromMilliseconds(_configManager.GetInt(arena.Cfg, "Misc", "RegionCheckInterval", 100) * 10);

                // Whether spectators can see extra data for the person they're spectating.
                ad.SpecSeeExtra = _configManager.GetInt(arena.Cfg, "Misc", "SpecSeeExtra", 1) != 0;

                // Whose energy levels spectators can see. The options are the
                // same as for Misc:SeeEnergy, with one addition: SEE_SPEC
                // means only the player you're spectating.
                ad.SpecSeeEnergy = (SeeEnergy)_configManager.GetInt(arena.Cfg, "Misc", "SpecSeeEnergy", (int)SeeEnergy.All);
                
                // Whose energy levels everyone can see: SEE_NONE means nobody
                // else's, SEE_ALL is everyone's, SEE_TEAM is only teammates.
                ad.AllSeeEnergy = (SeeEnergy)_configManager.GetInt(arena.Cfg, "Misc", "SeeEnergy", (int)SeeEnergy.None);

                ad.MaxDeathWithoutFiring = _configManager.GetInt(arena.Cfg, "Security", "MaxDeathWithoutFiring", 5);

                ad.NoSafeAntiwarp = _configManager.GetInt(arena.Cfg, "Misc", "NoSafeAntiwarp", 0) != 0;

                ad.WarpThresholdDelta = _configManager.GetInt(arena.Cfg, "Misc", "WarpTresholdDelta", 320);
                ad.WarpThresholdDelta *= ad.WarpThresholdDelta; // TODO: figure out why it's the value squared

                PersonalGreen pg = PersonalGreen.None;

                // Whether Thor greens don't go to the whole team.
                if (_configManager.GetInt(arena.Cfg, "Prize", "DontShareThor", 0) != 0)
                    pg |= PersonalGreen.Thor;

                // Whether Burst greens don't go to the whole team.
                if (_configManager.GetInt(arena.Cfg, "Prize", "DontShareBurst", 0) != 0)
                    pg |= PersonalGreen.Burst;

                // Whether Brick greens don't go to the whole team.
                if (_configManager.GetInt(arena.Cfg, "Prize", "DontShareBrick", 0) != 0)
                    pg |= PersonalGreen.Brick;

                ad.personalGreen = pg;

                // How far away to always send bullets (in pixels).
                int cfg_bulletpix = _configManager.GetInt(_configManager.Global, "Net", "BulletPixels", 1500);

                // How far away to always send weapons (in pixels).
                int cfg_wpnpix = _configManager.GetInt(_configManager.Global, "Net", "WeaponPixels", 2000);

                // How far away to send positions of players on radar.
                ad.cfg_pospix = _configManager.GetInt(_configManager.Global, "Net", "PositionExtraPixels", 8000);

                // Percent of position packets with antiwarp enabled to send to the whole arena.
                ad.cfg_sendanti = _configManager.GetInt(_configManager.Global, "Net", "AntiwarpSendPercent", 5);
                ad.cfg_sendanti = Constants.RandMax / 100 * ad.cfg_sendanti;

                int cfg_AntiwarpPixels = _configManager.GetInt(_configManager.Global, "Toggle", "AntiwarpPixels", 1);
                ad.cfg_AntiwarpRange = cfg_AntiwarpPixels * cfg_AntiwarpPixels;

                // continuum clients take EnterDelay + 100 ticks to respawn after death
                ad.cfg_EnterDelay = _configManager.GetInt(_configManager.Global, "Kill", "EnterDelay", 0) + 100;
                // setting of 0 or less means respawn in place, with 1 second delay
                if (ad.cfg_EnterDelay <= 0)
                    ad.cfg_EnterDelay = 100;


                for (int x = 0; x < ad.wpnRange.Length; x++)
                {
                    ad.wpnRange[x] = cfg_wpnpix;
                }

                // exceptions
                ad.wpnRange[(int)WeaponCodes.Bullet] = cfg_bulletpix;
                ad.wpnRange[(int)WeaponCodes.BounceBullet] = cfg_bulletpix;
                ad.wpnRange[(int)WeaponCodes.Thor] = 30000;

                if (action == ArenaAction.Create)
                    ad.initLockship = ad.initSpec = false;
            }
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (p == null)
                return;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            ArenaData ad = null;
            if (arena != null)
                ad = arena[_adkey] as ArenaData;

            if (action == PlayerAction.PreEnterArena)
            {
                // clear the saved ppk, but set time to the present so that new
                // position packets look like they're in the future. also set a
                // bunch of other timers to now.
                pd.pos.Time = ServerTick.Now;
                pd.lastRgnCheck = DateTime.UtcNow;

                pd.LastRegionSet = new Dictionary<MapRegion, bool>(_mapData.GetRegionCount(arena));

                pd.lockship = ad.initLockship;
                if (ad.initSpec)
                {
                    p.Ship = ShipType.Spec;
                    p.Freq = (short)arena.SpecFreq;
                }

                p.Attached = -1;

                _playerData.Lock();

                try
                {
                    p.Flags.IsDead = false;
                    p.LastDeath = new ServerTick(); // TODO: review this, looks strange since 0 is a valid value, maybe need nullable?
                    p.NextRespawn = new ServerTick(); // TODO: review this, looks strange since 0 is a valid value, maybe need nullable?
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
            else if (action == PlayerAction.EnterArena)
            {
                SeeEnergy seeNrg = SeeEnergy.None;
                SeeEnergy seeNrgSpec = SeeEnergy.None;
                bool seeEpd = false;

                if (ad.AllSeeEnergy != SeeEnergy.None)
                    seeNrg = ad.AllSeeEnergy;

                if (ad.SpecSeeEnergy != SeeEnergy.None)
                    seeNrgSpec = ad.SpecSeeEnergy;

                if (ad.SpecSeeExtra)
                    seeEpd = true;

                if (_capabilityManager != null)
                {
                    if (_capabilityManager.HasCapability(p, Constants.Capabilities.SeeEnergy))
                        seeNrg = seeNrgSpec = SeeEnergy.All;

                    if (_capabilityManager.HasCapability(p, Constants.Capabilities.SeeExtraPlayerData))
                        seeEpd = true;
                }

                pd.pl_epd.seeNrg = seeNrg;
                pd.pl_epd.seeNrgSpec = seeNrgSpec;
                pd.pl_epd.seeEpd = seeEpd;
                pd.epdQueries = 0;

                pd.wpnSent = 0;
                pd.deathWithoutFiring = 0;
                p.Flags.SentWeaponPacket = false;
            }
            else if (action == PlayerAction.LeaveArena)
            {
                lock (_specmtx)
                {
                    _playerData.Lock();
                    try
                    {
                        foreach (Player i in _playerData.PlayerList)
                        {
                            if (!(i[_pdkey] is PlayerData idata))
                                continue;

                            if (idata.speccing == p)
                                ClearSpeccing(idata);
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }

                    if (pd.epdQueries > 0)
                        _logManager.LogP(LogLevel.Error, nameof(Game), p, "extra position data queries is still nonzero");

                    ClearSpeccing(pd);
                }

                pd.LastRegionSet.Clear();
                //pd.lastrgnset.Clear();
            }
            else if(action == PlayerAction.EnterGame)
            {
                if (p.Ship != ShipType.Spec)
                {
                    DoSpawnCallback(p, SpawnCallback.SpawnReason.Initial);
                }
            }
        }

        private void Callback_NewPlayer(Player p, bool isNew)
        {
            if (p == null)
                return;

            if (p.Type == ClientType.Fake && !isNew)
            {
                // extra cleanup for fake players since LeaveArena isn't
                // called. fake players can't be speccing anyone else, but other
                // players can be speccing them.
                lock (_specmtx)
                {
                    _playerData.Lock();
                    try
                    {
                        foreach (Player i in _playerData.PlayerList)
                        {
                            if (!(i[_pdkey] is PlayerData idata))
                                continue;

                            if (idata.speccing == p)
                                ClearSpeccing(idata);
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }
                }
            }
        }

        private void ClearSpeccing(PlayerData data)
        {
            if (data == null)
                return;

            lock (_specmtx)
            {
                if (data.speccing == null)
                    return;

                try
                {
                    if (data.pl_epd.seeEpd)
                    {
                        if (!(data.speccing[_pdkey] is PlayerData odata))
                            return;

                        if (--odata.epdQueries <= 0)
                        {
                            // no more people speccing the player that want extra position data
                            _net.SendToOne(data.speccing, _clearSpecBytes, _clearSpecBytes.Length, NetSendFlags.Reliable);
                            odata.epdQueries = 0;
                        }
                    }
                }
                finally
                {
                    data.speccing = null;
                }
            }
        }

        private void AddSpeccing(PlayerData data, Player t)
        {
            lock (_specmtx)
            {
                data.speccing = t;

                if (data.pl_epd.seeEpd)
                {
                    if (!(t[_pdkey] is PlayerData tdata))
                        return;

                    if (tdata.epdQueries++ == 0)
                    {
                        // first time player is being specced by someone watching extra position data, tell the player to send extra position data
                        _net.SendToOne(t, _addSpecBytes, _addSpecBytes.Length, NetSendFlags.Reliable);
                    }
                }
            }
        }

        private void Packet_Position(Player p, byte[] data, int len)
        {
            HandlePositionPacket(p, new C2SPositionPacket(data), len, false);
        }

        private void HandlePositionPacket(Player p, C2SPositionPacket pos, int len, bool isFake)
        {
            if (p == null || p.Status != PlayerState.Playing)
                return;

#if CFG_RELAX_LENGTH_CHECKS
            if(len < C2SPositionPacket.Length)
#else
            if (len != C2SPositionPacket.Length && len != C2SPositionPacket.LengthWithExtra)
#endif
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "bad position packet len={0}", len);
                return;
            }

            Arena arena = p.Arena;
            if (arena == null || arena.Status != ArenaState.Running)
                return;

            if (!isFake)
            {
                // Verify checksum
                byte[] data = pos.RawData;
                byte checksum = 0;
                int left = 22;
                while ((left--) > 0)
                    checksum ^= data[left];

                if (checksum != 0)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "bad position packet checksum");
                    return;
                }
            }

            if (pos.X == -1 && pos.Y == -1)
            {
                // position sent after death, before respawn. these aren't
                // really useful for anything except making sure the server
                // knows the client hasn't dropped, so just ignore them.
                return;
            }

            Weapons weapon = pos.Weapon;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            if (!(arena[_adkey] is ArenaData ad))
                return;

            DateTime now = DateTime.UtcNow;
            ServerTick gtc = ServerTick.Now;

            // lag data
            if (_lagCollect != null && !isFake)
            {
                _lagCollect.Position(
                    p,
                    (gtc - pos.Time) * 10,
                    len >= 26 ? pos.Extra.S2CPing * 10 : -1,
                    pd.wpnSent);
            }

            bool isNewer = pos.Time > pd.pos.Time;

            // only copy if the new one is later
            if (isNewer || isFake)
            {
                // Safe zone
                if (((pos.Status ^ pd.pos.Status) & PlayerPositionStatus.Safezone) == PlayerPositionStatus.Safezone && !isFake)
                {
                    SafeZoneCallback.Fire(arena, p, pos.X, pos.Y, (pos.Status & PlayerPositionStatus.Safezone) == PlayerPositionStatus.Safezone);
                }

                // Warp
                if(((pos.Status ^ pd.pos.Status) & PlayerPositionStatus.Flash) == PlayerPositionStatus.Flash
                    && !isFake
                    && p.Ship != ShipType.Spec
                    && p.Ship == pd.PlayerPostitionPacket_LastShip
                    && p.Flags.SentPositionPacket
                    && !p.Flags.IsDead
                    && ad.WarpThresholdDelta > 0)
                {
                    int dx = pd.pos.X - pos.X;
                    int dy = pd.pos.Y - pos.Y;

                    if (dx * dx + dy * dy > ad.WarpThresholdDelta)
                    {
                        WarpCallback.Fire(arena, p, pd.pos.X, pd.pos.Y, pos.X, pos.Y);
                    }
                }

                // copy the whole thing. this will copy the epd, or, if the client
                // didn't send any epd, it will copy zeros because the buffer was
                // zeroed before data was recvd into it.
                pos.CopyTo(pd.pos);

                // update position in global player object.
                // only copy x/y if they are nonzero, so we keep track of last
                // non-zero position.
                if (pos.X != 0 || pos.Y != 0)
                {
                    p.Position.X = pos.X;
                    p.Position.Y = pos.Y;
                }

                p.Position.XSpeed = pos.XSpeed;
                p.Position.YSpeed = pos.YSpeed;
                p.Position.Rotation = pos.Rotation;
                p.Position.Bounty = pos.Bounty;
                p.Position.Status = pos.Status;
                p.Position.Energy = pos.Energy;
                p.Position.Time = pos.Time;
            }

            // check if it's the player's first position packet
            if (p.Flags.SentPositionPacket == false && !isFake)
            {
                p.Flags.SentPositionPacket = true;
                PlayerActionCallback.Fire(arena, p, PlayerAction.EnterGame, arena);
            }

            int latency = gtc - pos.Time;
            if (latency < 0)
                latency = 0;
            else if (latency > 255)
                latency = 255;

            int randnum = Rand();

            // spectators don't get their position sent to anyone
            if (p.Ship != ShipType.Spec)
            {
                int x1 = pos.X;
                int y1 = pos.Y;

                // update region-based stuff once in a while, for real players only
                if (isNewer 
                    && !isFake 
                    && (now - pd.lastRgnCheck) >= ad.RegionCheckTime)
                {
                    UpdateRegions(p, (short)(x1 >> 4), (short)(y1 >> 4));
                    pd.lastRgnCheck = now;
                }

                // this check should be before the weapon ignore hook
                if (pos.Weapon.Type != WeaponCodes.Null)
                {
                    p.Flags.SentWeaponPacket = true;
                    pd.deathWithoutFiring = 0;
                }

                // this is the weapons ignore hook.
                // also ignore weapons based on region
                if ((Rand() < pd.ignoreWeapons) 
                    || pd.MapRegionNoWeapons)
                {
                    weapon.Type = 0;
                }

                // also turn off anti based on region
                if (pd.MapRegionNoAnti)
                {
                    pos.Status &= ~PlayerPositionStatus.Antiwarp;
                }

                // if this is a plain position packet with no weapons, and is in
                // the wrong order, there's no need to send it. but fake players
                // never got data->pos.time initialized correctly, so do a
                // little special case.
                if (!isNewer && !isFake && weapon.Type == 0)
                    return;

                // TODO: PPK advisers - EditPPK

                // by default, send unreliable droppable packets. 
                // weapons get a higher priority.
                NetSendFlags nflags = NetSendFlags.Unreliable | NetSendFlags.Dropabble | 
                    (weapon.Type != WeaponCodes.Null ? NetSendFlags.PriorityP5 : NetSendFlags.PriorityP3);

                // there are several reasons to send a weapon packet (05) instead of just a position one (28)
                bool sendWeapon = (
                    (weapon.Type != WeaponCodes.Null) // a real weapon
                    || (pos.Bounty > byte.MaxValue) // bounty over 255
                    || (p.Id > byte.MaxValue)); //pid over 255

                // TODO: for arenas designed for a small # of players (e.g. 4v4 league), a way to always send to all? or is boosting weapon range settings to max good enough?
                bool sendToAll = false;

                // TODO: if a player is far away from the mine, how would that player know if the mine was cleared (detonated by another player outside of their range or repelled)?
                // would need a module to keep track of mine locations and do pseudo-region like comparisons? and use that know when to send a position packet to all?
                // what about bombs fired at low velocity? need wall collision detection?
                // TODO: a way for the server to keep track of where mines are currently placed so that it can replay packets to players that enter the arena? or does something like this exist already?

                // send mines to everyone
                if ((weapon.Type == WeaponCodes.Bomb || weapon.Type == WeaponCodes.ProxBomb) 
                    && weapon.Alternate)
                {
                    sendToAll = true;
                }

                // disable antiwarp if they're in a safe and NoSafeAntiwarp is on
                if ((pos.Status & PlayerPositionStatus.Antiwarp) == PlayerPositionStatus.Antiwarp
                    && (pos.Status & PlayerPositionStatus.Safezone) == PlayerPositionStatus.Safezone
                    && ad.NoSafeAntiwarp)
                {
                    pos.Status &= ~PlayerPositionStatus.Antiwarp;
                }

                // send some percent of antiwarp positions to everyone
                if ((weapon.Type == WeaponCodes.Null)
                    && ((pos.Status & PlayerPositionStatus.Antiwarp) == PlayerPositionStatus.Antiwarp)
                    && (Rand() < ad.cfg_sendanti))
                {
                    sendToAll = true;
                }

                // send safe zone enters to everyone, reliably
                // TODO: why? proposed send damage protocol? what about safe zone exits?
                if (((pos.Status & PlayerPositionStatus.Safezone) != 0) 
                    && ((p.Position.Status & PlayerPositionStatus.Safezone) == 0))
                {
                    sendToAll = true;
                    nflags = NetSendFlags.Reliable;
                }

                // send flashes to everyone, reliably
                if ((pos.Status & PlayerPositionStatus.Flash) != 0)
                {
                    sendToAll = true;
                    nflags = NetSendFlags.Reliable;
                }

                using DataBuffer copyBuffer = Pool<DataBuffer>.Default.Get();
                C2SPositionPacket copy = new C2SPositionPacket(copyBuffer.Bytes);

                using DataBuffer wpnBuffer = Pool<DataBuffer>.Default.Get();
                S2CWeaponsPacket wpn = new S2CWeaponsPacket(wpnBuffer.Bytes);

                using DataBuffer sendposBuffer = Pool<DataBuffer>.Default.Get();
                S2CPositionPacket sendpos = new S2CPositionPacket(sendposBuffer.Bytes);

                // ensure that all packets get build before use
                bool modified = true;
                bool wpnDirty = true;
                bool posDirty = true;

                _playerData.Lock();

                try
                {
                    // have to do this check inside pd->Lock();
                    // ignore packets from the first 500ms of death, and accept packets up to 500ms
                    // before their expected respawn. 
                    if (p.Flags.IsDead && gtc - p.LastDeath >= 50 && p.NextRespawn - gtc <= 50)
                    {
                        p.Flags.IsDead = false;
                        DoSpawnCallback(p, SpawnCallback.SpawnReason.AfterDeath);
                    }

                    foreach (Player i in _playerData.PlayerList)
                    {
                        if (!(i[_pdkey] is PlayerData idata))
                            continue;

                        if (i.Status == PlayerState.Playing
                            && i.IsStandard
                            && i.Arena == arena
                            && (i != p || p.Flags.SeeOwnPosition))
                        {
                            int dist = Hypot(x1 - idata.pos.X, y1 - idata.pos.Y);
                            int range;

                            // determine the packet range
                            if (sendWeapon && pos.Weapon.Type != WeaponCodes.Null)
                                range = ad.wpnRange[(int)pos.Weapon.Type];
                            else
                                range = i.Xres + i.Yres;

                            if (dist <= range
                                || sendToAll
                                || idata.speccing == p // always send to spectators of the player
                                || i.Attached == p.Id // always send to turreters
                                || (pos.Weapon.Type == WeaponCodes.Null
                                    && dist < ad.cfg_pospix
                                    && randnum > ((double)dist / (double)ad.cfg_pospix * Constants.RandMax + 1d)) // send some radar packets
                                || i.Flags.SeeAllPositionPackets) // bots
                            {
                                int extralen;

                                const int plainlen = 0;
                                const int nrglen = 2;
                                int epdlen = ExtraPositionData.Length;

                                if (i.Ship == ShipType.Spec)
                                {
                                    if (idata.pl_epd.seeEpd && idata.speccing == p)
                                    {
                                        extralen = (len >= C2SPositionPacket.LengthWithExtra) ? epdlen : nrglen;
                                    }
                                    else if (idata.pl_epd.seeNrgSpec == SeeEnergy.All
                                        || (idata.pl_epd.seeNrgSpec == SeeEnergy.Team
                                            && p.Freq == i.Freq)
                                        || (idata.pl_epd.seeNrgSpec == SeeEnergy.Spec
                                            && idata.speccing == p)) // TODO: I think ASSS has a bug which is fixed here
                                    {
                                        extralen = nrglen;
                                    }
                                    else
                                    {
                                        extralen = plainlen;
                                    }
                                }
                                else if (idata.pl_epd.seeNrg == SeeEnergy.All
                                    || (idata.pl_epd.seeNrg == SeeEnergy.Team
                                        && p.Freq == i.Freq))
                                {
                                    extralen = nrglen;
                                }
                                else
                                {
                                    extralen = plainlen;
                                }

                                if (modified)
                                {
                                    pos.CopyTo(copy);
                                    modified = false;
                                }

                                bool drop = false;

                                // TODO: PPK advisers - EditIndividualPPK

                                wpnDirty = wpnDirty || modified;
                                posDirty = posDirty || modified;

                                if (!drop)
                                {
                                    if ((!modified && sendWeapon)
                                        || copy.Weapon.Type > 0
                                        || (copy.Bounty & 0xFF00) != 0
                                        || (p.Id & 0xFF00) != 0)
                                    {
                                        int length = S2CWeaponsPacket.Length + extralen;

                                        if (wpnDirty)
                                        {
                                            wpn.Type = (byte)S2CPacketType.Weapon;
                                            wpn.Rotation = copy.Rotation;
                                            wpn.Time = (ushort)(gtc & 0xFFFF);
                                            wpn.X = copy.X;
                                            wpn.YSpeed = copy.YSpeed;
                                            wpn.PlayerId = (ushort)p.Id;
                                            wpn.XSpeed = copy.XSpeed;
                                            wpn.Checksum = 0;
                                            wpn.Status = copy.Status;
                                            wpn.C2SLatency = (byte)latency;
                                            wpn.Y = copy.Y;
                                            wpn.Bounty = copy.Bounty;
                                            wpn.Weapon = pos.Weapon;
                                            wpn.Extra = copy.Extra;

                                            // move this field from the main packet to the extra data, in case they don't match.
                                            ExtraPositionData epd = wpn.Extra;
                                            epd.Energy = (ushort)copy.Energy;

                                            wpnDirty = modified;

                                            wpn.DoChecksum();
                                        }

                                        if (wpn.Weapon.Type != 0)
                                            idata.wpnSent++;

                                        _net.SendToOne(i, wpnBuffer.Bytes, length, nflags);
                                    }
                                    else
                                    {
                                        int length = S2CPositionPacket.Length + extralen;
                                        if (posDirty)
                                        {
                                            sendpos.Type = (byte)S2CPacketType.Position;
                                            sendpos.Rotation = copy.Rotation;
                                            sendpos.Time = (ushort)(gtc & 0xFFFF);
                                            sendpos.X = copy.X;
                                            sendpos.C2SLatency = (byte)latency;
                                            sendpos.Bounty = (byte)copy.Bounty;
                                            sendpos.PlayerId = (byte)p.Id;
                                            sendpos.Status = copy.Status;
                                            sendpos.YSpeed = copy.YSpeed;
                                            sendpos.Y = copy.Y;
                                            sendpos.XSpeed = copy.XSpeed;
                                            sendpos.Extra = copy.Extra;

                                            // move this field from the main packet to the extra data, in case they don't match.
                                            ExtraPositionData epd = copy.Extra;
                                            epd.Energy = (ushort)copy.Energy;

                                            posDirty = modified;
                                        }

                                        _net.SendToOne(i, sendposBuffer.Bytes, length, nflags);
                                    }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }

            pd.PlayerPostitionPacket_LastShip = p.Ship;
        }

        private void UpdateRegions(Player p, short x, short y)
        {
            if (p == null)
                return;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            Arena arena = p.Arena;
            /*
            foreach (MapRegion region in pd.LastRegionSet.Keys)
            {
                pd.LastRegionSet[region] = false;
            }
            */
            foreach(MapRegion region in _mapData.RegionsAt(p.Arena, x, y))
            {
                if (region.NoAntiwarp)
                    pd.MapRegionNoAnti = true;

                if (region.NoWeapons)
                    pd.MapRegionNoWeapons = true;

                //if (!pd.LastRegionSet.ContainsKey(region))
                //{
                    MapRegionCallback.Fire(arena, p, region, x, y, true);
                    //pd.LastRegionSet.Add(region, true);
                //}
                //else
                //{
                    //pd.LastRegionSet[region] = true;
                //}
            }

            //pd.LastRegionSet.Except(
            //_mapData.RegionsAt(p.Arena, (short)x, (short)y)

            // asss does a merge join to figure out the differences
            //TODO: handle the callbacks properly, this is just for testing autowarp...
            //foreach (KeyValuePair<MapRegion, bool> kvp in pd.LastRegionSet)
            //{
                //if (kvp.Value == false)
                    //MapRegionCallback.Fire(arena, p, kvp.Key, x, y, false);
            //}
            // TODO: region enter and leave callbacks
        }

        private void Packet_SpecRequest(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len != 3)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "bad spec req packet len={0}", len);
                return;
            }

            if (p.Status != PlayerState.Playing || p.Ship != ShipType.Spec)
                return;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            SimplePacket pkt = new SimplePacket(data);
            int tpid = pkt.D1;

            lock (_specmtx)
            {
                ClearSpeccing(pd);

                if (tpid >= 0)
                {
                    Player t = _playerData.PidToPlayer(tpid);
                    if (t != null && t.Status == PlayerState.Playing && t.Ship != ShipType.Spec && t.Arena == p.Arena)
                        AddSpeccing(pd, t);
                }
            }
        }

        private void Packet_SetShip(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len != 2)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "bad ship req packet len={0}", len);
                return;
            }

            Arena arena = p.Arena;
            if (p.Status != PlayerState.Playing || arena == null)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Game), p, "state sync problem: ship request from bad status");
                return;
            }

            if (!(p[_pdkey] is PlayerData pd))
                return;

            ShipType ship = (ShipType)data[1];
            if (ship < ShipType.Warbird || ship > ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "bad ship number: {0}", ship);
                return;
            }

            short freq = p.Freq;

            lock (_freqshipmtx)
            {
                if (p.Flags.DuringChange)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Game), p, "state sync problem: ship request before ack from previous change");
                    return;
                }

                if (ship == p.Ship)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Game), p, "state sync problem: already in requested ship");
                    return;
                }

                // do this bit while holding the mutex. it's ok to check the flag afterwards, though.
                ExpireLock(p);
            }

            // checked lock state (but always allow switching to spec)
            if (pd.lockship &&
                ship != ShipType.Spec &&
                !(_capabilityManager != null && _capabilityManager.HasCapability(p, Constants.Capabilities.BypassLock)))
            {
                if (_chat != null)
                    _chat.SendMessage(p, "You have been locked in {0}.", p.Ship == ShipType.Spec ? "spectator mode" : "your ship");

                return;
            }

            
            IFreqManager fm = _broker.GetInterface<IFreqManager>();
            if (fm != null)
            {
                try
                {
                    // TODO: IFreqManager has changed in ASSS
                    fm.ShipChange(p, ref ship, ref freq);
                }
                finally
                {
                    _broker.ReleaseInterface(ref fm);
                }
            }

            SetShipAndFreq(p, ship, freq);
        }

        private void Packet_SetFreq(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (len != 3)
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "bad freq req packet len={0}", len);
            else if (p.Flags.DuringChange)
                _logManager.LogP(LogLevel.Warn, nameof(Game), p, "state sync problem: freq change before ack from previous change");
            else
            {
                SimplePacket pkt = new SimplePacket(data);
                FreqChangeRequest(p, pkt.D1);
            }
        }

        private void FreqChangeRequest(Player p, short freq)
        {
            if(p == null)
                return;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            Arena arena = p.Arena;
            ShipType ship = p.Ship;

            if (p.Status != PlayerState.Playing || arena == null)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "freq change from bad arena");
                return;
            }

            // check lock state
            lock (_freqshipmtx)
            {
                ExpireLock(p);
            }

            if (pd.lockship &&
                !(_capabilityManager != null && _capabilityManager.HasCapability(p, Constants.Capabilities.BypassLock)))
            {
                if (_chat != null)
                    _chat.SendMessage(p, "You have been locked in {0}.", (p.Ship == ShipType.Spec) ? "spectator mode" : "your ship");
                return;
            }

            IFreqManager fm = _broker.GetInterface<IFreqManager>();
            if (fm != null)
            {
                try
                {
                    // TODO: IFreqManager changed in ASSS
                    fm.FreqChange(p, ref ship, ref freq);
                }
                finally
                {
                    _broker.ReleaseInterface(ref fm);
                }
            }
            else
            {
                SetFreq(p, freq);
            }
        }

        private void SetShipAndFreq(Player p, ShipType ship, short freq)
        {
            if (p == null)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            ShipType oldShip = p.Ship;
            short oldFreq = p.Freq;
            SpawnCallback.SpawnReason flags = SpawnCallback.SpawnReason.ShipChange;

            if (p.Type == ClientType.Chat && ship != ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Game), p, "someone tried to forced chat client into playing ship");
                return;
            }

            if (freq < 0 || freq > 9999 || ship < 0 || ship > ShipType.Spec)
                return;

            lock (_freqshipmtx)
            {
                if (p.Ship == ship &&
                    p.Freq == freq)
                {
                    // nothing to do
                    return;
                }

                if (p.IsStandard)
                    p.Flags.DuringChange = true;

                p.Ship = ship;
                p.Freq = freq;

                lock (_specmtx)
                {
                    ClearSpeccing(pd);
                }
            }

            using(DataBuffer buffer = Pool<DataBuffer>.Default.Get())
            {
                ShipChangePacket to = new ShipChangePacket(buffer.Bytes);
                to.Type = (byte)S2CPacketType.ShipChange;
                to.ShipType = (sbyte)ship;
                to.Pid = (short)p.Id;
                to.Freq = freq;
                
                if (p.IsStandard)
                {
                    // send it to him, with a callback
                    _net.SendWithCallback(p, buffer.Bytes, ShipChangePacket.Length, ResetDuringChange);
                }

                // send it to everyone else
                _net.SendToArena(arena, p, buffer.Bytes, ShipChangePacket.Length, NetSendFlags.Reliable);

                //if(_chatnet != null)

            }

            PreShipFreqChangeCallback.Fire(arena, p, ship, oldShip, freq, oldFreq);
            DoShipFreqChangeCallback(p, ship, oldShip, freq, oldFreq);

            // now setup for the CB_SPAWN callback
            _playerData.Lock();

            try
            {
                if (p.Flags.IsDead)
                {
                    flags |= SpawnCallback.SpawnReason.AfterDeath;
                }

                // a shipchange will revive a dead player
                p.Flags.IsDead = false;
            }
            finally
            {
                _playerData.Unlock();
            }

            if (ship != ShipType.Spec)
            {
                // flags = SPAWN_SHIPCHANGE set at the top of the function
                if (oldShip == ShipType.Spec)
                {
                    flags |= SpawnCallback.SpawnReason.Initial;
                }

                DoSpawnCallback(p, flags);
            }

            _logManager.LogP(LogLevel.Info, nameof(Game), p, "changed ship/freq to ship {0}, freq {1}", ship, freq);
        }

        private void SetFreq(Player p, short freq)
        {
            if (p == null)
                return;

            if (freq < 0 || freq > 9999)
                return;

            Arena arena = p.Arena;
            if(arena == null)
                return;

            short oldFreq = p.Freq;

            lock (_freqshipmtx)
            {
                if (p.Freq == freq)
                    return;

                if (p.IsStandard)
                    p.Flags.DuringChange = true;

                p.Freq = freq;
            }

            
            using (DataBuffer buffer = Pool<DataBuffer>.Default.Get())
            {
                SimplePacket sp = new SimplePacket(buffer.Bytes);
                sp.Type = (byte)S2CPacketType.FreqChange;
                sp.D1 = (short)p.Id;
                sp.D2 = freq;
                sp.D3 = -1;

                // him with callback
                if (p.IsStandard)
                    _net.SendWithCallback(p, buffer.Bytes, 6, ResetDuringChange);
                
                // everyone else
                _net.SendToArena(arena, p, buffer.Bytes, 6, NetSendFlags.Reliable);

                //if(_chatNet != null)

            }

            PreShipFreqChangeCallback.Fire(arena, p, p.Ship, p.Ship, freq, oldFreq);
            DoShipFreqChangeCallback(p, p.Ship, p.Ship, freq, oldFreq);

            _logManager.LogP(LogLevel.Info, nameof(Game), p, "changed freq to {0}", freq);
        }

        private void ResetDuringChange(Player p, bool success)
        {
            if (p == null)
                return;

            lock (_freqshipmtx)
            {
                p.Flags.DuringChange = false;
            }
        }

        private void ExpireLock(Player p)
        {
            if (p == null)
                return;

            if (!(p[_pdkey] is PlayerData pd))
                return;

            lock (_freqshipmtx)
            {
                if(pd.expires != null)
                    if (DateTime.UtcNow > pd.expires)
                    {
                        pd.lockship = false;
                        pd.expires = null;
                        _logManager.LogP(LogLevel.Drivel, nameof(Game), p, "lock expired");
                    }
            }
        }

        private void Packet_Die(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len != 5)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "bad death packet len={0}", len);
                return;
            }

            if (p.Status != PlayerState.Playing)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            if (!(arena[_adkey] is ArenaData ad))
                return;

            SimplePacket dead = new SimplePacket(data);
            short bty = dead.D2;

            Player killer = _playerData.PidToPlayer(dead.D1);
            if (killer == null || killer.Status != PlayerState.Playing || killer.Arena != arena)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "reported kill by bad pid {0}", dead.D1);
                return;
            }

            short flagCount = p.pkt.FlagsCarried;

            // these flags are primarily for the benefit of other modules
            _playerData.Lock();

            try
            {
                p.Flags.IsDead = true;
                p.LastDeath = ServerTick.Now;
                p.NextRespawn = p.LastDeath + (uint)ad.cfg_EnterDelay;
            }
            finally
            {
                _playerData.Unlock();
            }

            // TODO: kill adviser EditDeath

            Prize green;

            if ((p.Freq == killer.Freq) && (_configManager.GetInt(arena.Cfg, "Prize", "UseTeamkillPrize", 0) != 0))
            {
                green = (Prize)_configManager.GetInt(arena.Cfg, "Prize", "TeamkillPrize", 0);
            }
            else
            {
                IClientSettings cset = arena.GetInterface<IClientSettings>();
                if (cset != null)
                {
                    try
                    {
                        green = cset.GetRandomPrize(arena);
                    }
                    finally
                    {
                        arena.ReleaseInterface(ref cset);
                    }
                }
                else
                {
                    green = 0;
                }
            }

            // TODO: kill adviser KillPoints

            // this will figure out how many points to send in the packet
            // NOTE: asss uses the event to set the points and green, i think it's best to keep it split between an interface call and event
            short pts = 0;
            IKillPoints kp = arena.GetInterface<IKillPoints>();
            if (kp != null)
            {
                try
                {
                    kp.GetKillPoints(arena, killer, p, bty, flagCount, out pts, out Prize g);
                    green = g;
                }
                finally
                {
                    arena.ReleaseInterface(ref kp);
                }
            }

            // allow a module to modify the green sent in the packet
            IKillGreen killGreen = arena.GetInterface<IKillGreen>();
            if (killGreen != null)
            {
                try
                {
                    killGreen.KillGreen(arena, killer, p, bty, flagCount, pts, green);
                }
                finally
                {
                }
            }

            // record the kill points on our side
            if (pts != 0)
            {
                IStats stats = _broker.GetInterface<IStats>();
                if (stats != null)
                {
                    try
                    {
                        // TODO: 
                        //stats.IncrementStats(killer, 
                    }
                    finally
                    {
                        _broker.ReleaseInterface(ref stats);
                    }
                }
            }

            FireKillEvent(arena, killer, p, bty, flagCount, pts, green);

            NotifyKill(killer, p, pts, flagCount, green);

            FirePostKillEvent(arena, killer, p, bty, flagCount, pts, green);

            _logManager.LogA(LogLevel.Info, nameof(Game), arena, "{0} killed by {1} (bty={2},flags={3},pts={4})", p.Name, killer.Name, bty, flagCount, pts);

            if (p.Flags.SentWeaponPacket == false)
            {
                if (p[_pdkey] is PlayerData pd)
                {
                    if (pd.deathWithoutFiring++ == ad.MaxDeathWithoutFiring)
                    {
                        _logManager.LogP(LogLevel.Info, nameof(Game), p, "specced for too many deaths without firing");
                        SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);
                    }
                }
            }

            // reset this so we can accurately check deaths without firing
            p.Flags.SentWeaponPacket = false;
        }

        private void FireKillEvent(Arena arena, Player killer, Player killed, short bty, short flagCount, short pts, Prize green)
        {
            if (arena == null || killer == null || killed == null)
                return;

            KillCallback.Fire(arena, arena, killer, killed, bty, flagCount, pts, green);
        }

        private void FirePostKillEvent(Arena arena, Player killer, Player killed, short bty, short flagCount, short pts, Prize green)
        {
            if (arena == null || killer == null || killed == null)
                return;

            PostKillCallback.Fire(arena, arena, killer, killed, bty, flagCount, pts, green);
        }

        private void NotifyKill(Player killer, Player killed, short pts, short flagCount, Prize green)
        {
            if (killer == null || killed == null)
                return;

            using(DataBuffer buffer = Pool<DataBuffer>.Default.Get())
            {
                KillPacket kp = new KillPacket(buffer.Bytes);
                kp.Type = (byte)S2CPacketType.Kill;
                kp.Green = green;
                kp.Killer = (short)killer.Id;
                kp.Killed = (short)killed.Id;
                kp.Bounty = pts;
                kp.Flags = flagCount;

                _net.SendToArena(killer.Arena, null, buffer.Bytes, KillPacket.Length, NetSendFlags.Reliable);
            }

            //if(_chatnet != null)
        }

        private void Packet_Green(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len != GreenPacket.C2SLength)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "bad green packet len={0}", len);
                return;
            }

            if (p.Status != PlayerState.Playing)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            if (!(arena[_adkey] is ArenaData ad))
                return;

            GreenPacket g = new GreenPacket(data);
            Prize prize = g.Green;
            
            // don't forward non-shared prizes
            if(!(prize == Prize.Thor && (ad.personalGreen & PersonalGreen.Thor) == PersonalGreen.Thor) &&
                !(prize == Prize.Burst && (ad.personalGreen & PersonalGreen.Burst) == PersonalGreen.Burst) &&
                !(prize == Prize.Brick && (ad.personalGreen & PersonalGreen.Brick) == PersonalGreen.Brick))
            {
                g.Pid = (short)p.Id;
                g.Type = (byte)S2CPacketType.Green; // HACK: reuse the buffer that it came in on
                _net.SendToArena(arena, p, data, GreenPacket.S2CLength, NetSendFlags.Unreliable);
                //g.Type = C2SPacketType.Green; // asss sets it back, i dont think this is necessary though
            }

            FireGreenEvent(arena, p, g.X, g.Y, prize);
        }

        private void FireGreenEvent(Arena arena, Player p, int x, int y, Prize prize)
        {
            if(p == null)
                return;

            if(arena != null)
                GreenCallback.Fire(arena, p, x, y, prize);
            else
                GreenCallback.Fire(_broker, p, x, y, prize);
        }

        private void Packet_AttachTo(Player p, byte[] data, int len)
        {
            if (p == null || data == null)
                return;

            if (len != 3)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "bad attach req packet len={0}", len);
                return;
            }

            if (p.Status != PlayerState.Playing)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            SimplePacket sp = new SimplePacket(data);
            short pid2 = sp.D1;

            Player to = null;

            // -1 means detaching
            if (pid2 != -1)
            {
                to = _playerData.PidToPlayer(pid2);
                if (to == null ||
                    to.Status != PlayerState.Playing ||
                    to == p ||
                    p.Arena != to.Arena ||
                    p.Freq != to.Freq)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "tried to attach to bad pid {0}", pid2);
                    return;
                }
            }

            // only send it if state has changed
            if (p.Attached != pid2)
            {
                sp.Type = (byte)S2CPacketType.Turret;
                sp.D1 = (short)p.Id;
                sp.D2 = pid2;
                _net.SendToArena(arena, null, data, 5, NetSendFlags.Reliable);
                p.Attached = pid2;

                AttachCallback.Fire(arena, p, to);
            }
        }

        private void Packet_TurretKickoff(Player p, byte[] data, int len)
        {
            if (p == null || data == null)
                return;

            if (p.Status != PlayerState.Playing)
                return;

            Arena arena = p.Arena;
            if(arena == null)
                return;

            SimplePacket sp = new SimplePacket(data);
            sp.Type = (byte)S2CPacketType.TurretKickoff;
            sp.D1 = (short)p.Id;

            _net.SendToArena(arena, null, data, 3, NetSendFlags.Reliable);
        }

        private int Hypot(int dx, int dy)
        {
            uint dd = (uint)((dx * dx) + (dy * dy));

            if (dx < 0) dx = -dx;
            if (dy < 0) dy = -dy;

            // initial hypotenuse guess (from Gems)
            uint r = (uint)((dx > dy) ? (dx + (dy >> 1)) : (dy + (dx >> 1)));

            if (r == 0) return (int)r;

            // converge 3 times
            r = (dd / r + r) >> 1;
            r = (dd / r + r) >> 1;
            r = (dd / r + r) >> 1;

            return (int)r;
        }

        private int Rand()
        {
            lock (random)
            {
                return random.Next(Constants.RandMax + 1); // +1 since it's exclusive
            }
        }

        private void LockWork(ITarget target, bool nval, bool notify, bool spec, int timeout)
        {
            _playerData.TargetToSet(target, out LinkedList<Player> set);
            if (set == null)
                return;

            foreach (Player p in set)
            {
                if (!(p[_pdkey] is PlayerData pd))
                    continue;

                if (spec && (p.Arena != null) && (p.Ship != ShipType.Spec))
                    SetShipAndFreq(p, ShipType.Spec, p.Arena.SpecFreq);

                if (notify && (pd.lockship != nval) && (_chat != null))
                {
                    _chat.SendMessage(p, nval ?
                        (p.Ship == ShipType.Spec ?
                        "You have been locked to spectator mode." :
                        "You have been locked to your ship.") :
                        "Your ship has been unlocked.");
                }

                pd.lockship = nval;
                if(nval == false || timeout == 0)
                    pd.expires = null;
                else
                    pd.expires =  DateTime.UtcNow.AddSeconds(timeout);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = null,
            Description =
            "Displays players spectating you. When private, displays players\n" +
            "spectating the target.")]
        private void Command_spec(string command, string parameters, Player p, ITarget target)
        {
            Player targetPlayer = null;
            
            if(target.Type == TargetType.Player)
                targetPlayer = (target as IPlayerTarget).Player;

            if (targetPlayer == null)
                targetPlayer = p;

            int specCount = 0;
            StringBuilder sb = new StringBuilder();

            _playerData.Lock();
            try
            {
                foreach(Player playerToCheck in _playerData.PlayerList)
                {
                    PlayerData pd = playerToCheck[_pdkey] as PlayerData;
                    if (pd != null)
                        continue;

                    if ((pd.speccing == targetPlayer) &&
                        (!_capabilityManager.HasCapability(playerToCheck, Constants.Capabilities.InvisibleSpectator) ||
                        _capabilityManager.HigherThan(p, playerToCheck)))
                    {
                        specCount++;

                        if (sb.Length > 0)
                            sb.Append(", ");

                        sb.Append(targetPlayer.Name);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            if (specCount > 1)
            {
                _chat.SendMessage(p, "{0} spectators: ", specCount);
                _chat.SendWrappedText(p, sb.ToString());
            }
            else if (specCount == 1)
            {
                _chat.SendMessage(p, "1 spectator: {0}", sb.ToString());
            }
            else if (p == targetPlayer)
            {
                _chat.SendMessage(p, "No players are spectating you.");
            }
            else
            {
                _chat.SendMessage(p, "No players are spectating {0}.", targetPlayer.Name);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Arena | CommandTarget.Player,
            Args = "[-t] [-n] [-s]",
            Description =
            "If sent as a priv message, turns energy viewing on for that player.\n" +
            "If sent as a pub message, turns energy viewing on for the whole arena\n" +
            "(note that this will only affect new players entering the arena).\n" +
            "If {-t} is given, turns energy viewing on for teammates only.\n" +
            "If {-n} is given, turns energy viewing off.\n" + 
            "If {-s} is given, turns energy viewing on/off for spectator mode.\n")]
        private void Command_energy(string command, string parameters, Player p, ITarget target)
        {
            Player targetPlayer = null;

            if (target.Type == TargetType.Player)
                targetPlayer = (target as IPlayerTarget).Player;

            SeeEnergy nval = SeeEnergy.All;
            bool spec = false;

            if(!string.IsNullOrEmpty(parameters) && parameters.Contains("-t"))
                nval = SeeEnergy.Team;

            if(!string.IsNullOrEmpty(parameters) && parameters.Contains("-n"))
                nval = SeeEnergy.None;

            if(!string.IsNullOrEmpty(parameters) && parameters.Contains("-s"))
                spec = true;

            if (targetPlayer != null)
            {
                PlayerData pd = targetPlayer[_pdkey] as PlayerData;
                if (pd != null)
                    return;

                if (spec)
                    pd.pl_epd.seeNrgSpec = nval;
                else
                    pd.pl_epd.seeNrg = nval;
            }
            else
            {
                if (!(p.Arena[_adkey] is ArenaData ad))
                    return;

                if (spec)
                    ad.SpecSeeEnergy = nval;
                else
                    ad.SpecSeeEnergy = nval;
            }
        }

        private void DoSpawnCallback(Player p, SpawnCallback.SpawnReason reason)
        {
            _mainloop.QueueMainWorkItem(
                MainloopWork_RunSpawnCallback,
                new SpawnDTO()
                {
                    Arena = p.Arena,
                    Player = p,
                    Reason = reason,
                });
        }

        private struct SpawnDTO
        {
            public Arena Arena;
            public Player Player;
            public SpawnCallback.SpawnReason Reason;
        }

        private void MainloopWork_RunSpawnCallback(SpawnDTO dto)
        {
            if (dto.Arena == dto.Player.Arena)
            {
                SpawnCallback.Fire(dto.Arena, dto.Player, dto.Reason);
            }
        }

        private struct ShipFreqChangeDTO
        {
            public Arena Arena;
            public Player Player;
            public ShipType NewShip;
            public ShipType OldShip;
            public short NewFreq;
            public short OldFreq;
        }

        private void DoShipFreqChangeCallback(Player p, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            _mainloop.QueueMainWorkItem(
                MainloopWork_RunShipFreqChangeCallback,
                new ShipFreqChangeDTO()
                {
                    Arena = p.Arena,
                    Player = p,
                    NewShip = newShip,
                    OldShip = oldShip,
                    NewFreq = newFreq,
                    OldFreq = oldFreq,
                });
        }

        private void MainloopWork_RunShipFreqChangeCallback(ShipFreqChangeDTO dto)
        {
            if (dto.Arena == dto.Player.Arena)
            {
                ShipFreqChangeCallback.Fire(dto.Arena, dto.Player, dto.NewShip, dto.OldShip, dto.NewFreq, dto.OldFreq);
            }
        }
    }
}

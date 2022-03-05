using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SSProto = SS.Core.Persist.Protobuf;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class Game : IModule, IGame
    {
        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private ICapabilityManager _capabilityManager;
        private IChat _chat;
        //private IChatNet _chatnet;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private ILagCollect _lagCollect;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IMapData _mapData;
        private INetwork _net;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IPrng _prng;

        private IPersist _persist;

        private InterfaceRegistrationToken<IGame> _iGameToken;

        private int _pdkey;
        private int _adkey;

        private DelegatePersistentData<Player> _persistRegistration;

        private readonly object _specmtx = new();
        private readonly object _freqshipmtx = new();

        private const int WeaponCount = 32;

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
            public C2S_PositionPacket pos = new();

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

            /// <summary>
            /// Set of regions the player was in during the last region check.
            /// </summary>
            public IImmutableSet<MapRegion> LastRegionSet = ImmutableHashSet<MapRegion>.Empty;

            public ShipType? PlayerPostitionPacket_LastShip;
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

        // delegates to prevent allocating a new delegate object per call
        private readonly Action<ShipFreqChangeDTO> mainloopWork_RunShipFreqChangeCallbackAction;
        private readonly Action<SpawnDTO> mainloopWork_RunSpawnCallbackAction;

        public Game()
        {
            mainloopWork_RunSpawnCallbackAction = MainloopWork_RunSpawnCallback;
            mainloopWork_RunShipFreqChangeCallbackAction = MainloopWork_RunShipFreqChangeCallback;
        }

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            ICapabilityManager capabilityManager,
            IChat chat,
            //IChatNet chatnet,
            ICommandManager commandManager, 
            IConfigManager configManager,
            ILagCollect lagCollect,
            ILogManager logManager,
            IMainloop mainloop,
            IMapData mapData,
            INetwork net,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IPrng prng)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            //_chatnet = chatnet ?? throw new ArgumentNullException(nameof(chatnet));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _lagCollect = lagCollect ?? throw new ArgumentNullException(nameof(lagCollect));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));

            _persist = broker.GetInterface<IPersist>();

            _adkey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdkey = _playerData.AllocatePlayerData<PlayerData>();

            if (_persist != null)
            {
                _persistRegistration = new(
                    (int)PersistKey.GameShipLock, PersistInterval.ForeverNotShared, PersistScope.PerArena, Persist_GetShipLockData, Persist_SetShipLockData, null);

                _persist.RegisterPersistentData(_persistRegistration);
            }

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            PlayerActionCallback.Register(_broker, Callback_PlayerAction);
            NewPlayerCallback.Register(_broker, Callback_NewPlayer);

            _net.AddPacket(C2SPacketType.Position, Packet_Position);
            _net.AddPacket(C2SPacketType.SpecRequest, Packet_SpecRequest);
            _net.AddPacket(C2SPacketType.SetShip, Packet_SetShip);
            _net.AddPacket(C2SPacketType.SetFreq, Packet_SetFreq);
            _net.AddPacket(C2SPacketType.Die, Packet_Die);
            _net.AddPacket(C2SPacketType.Green, Packet_Green);
            _net.AddPacket(C2SPacketType.AttachTo, Packet_AttachTo);
            _net.AddPacket(C2SPacketType.TurretKickOff, Packet_TurretKickoff);

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
            if (broker.UnregisterInterface(ref _iGameToken) != 0)
                return false;

            //if(_chatnet != null)

            if (_commandManager != null)
            {
                _commandManager.RemoveCommand("spec", Command_spec, null);
                _commandManager.RemoveCommand("energy", Command_energy, null);
            }

            _net.RemovePacket(C2SPacketType.Position, Packet_Position);
            _net.RemovePacket(C2SPacketType.SpecRequest, Packet_SpecRequest);
            _net.RemovePacket(C2SPacketType.SetShip, Packet_SetShip);
            _net.RemovePacket(C2SPacketType.SetFreq, Packet_SetFreq);
            _net.RemovePacket(C2SPacketType.Die, Packet_Die);
            _net.RemovePacket(C2SPacketType.Green, Packet_Green);
            _net.RemovePacket(C2SPacketType.AttachTo, Packet_AttachTo);
            _net.RemovePacket(C2SPacketType.TurretKickOff, Packet_TurretKickoff);

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);
            NewPlayerCallback.Unregister(_broker, Callback_NewPlayer);

            _mainloop.WaitForMainWorkItemDrain();

            if (_persist != null && _persistRegistration != null)
            {
                _persist.UnregisterPersistentData(_persistRegistration);
                broker.ReleaseInterface(ref _persist);
            }

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
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            S2C_WarpTo warpTo = new(x, y);
            _net.SendToTarget(target, ref warpTo, NetSendFlags.Reliable | NetSendFlags.Urgent);
        }

        void IGame.GivePrize(ITarget target, Prize prize, short count)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            S2C_PrizeReceive packet = new(count, prize);
            _net.SendToTarget(target, ref packet, NetSendFlags.Reliable);
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
            if (p == null || p[_pdkey] is not PlayerData pd)
                return false;

            return pd.lockship;
        }

        void IGame.LockArena(Arena arena, bool notify, bool onlyArenaState, bool initial, bool spec)
        {
            if (arena[_adkey] is not ArenaData ad)
                return;

            ad.initLockship = true;
            if (!initial)
                ad.initSpec = true;

            if (!onlyArenaState)
            {
                LockWork(arena, true, notify, spec, 0);
            }
        }

        void IGame.UnlockArena(Arena arena, bool notify, bool onlyArenaState)
        {
            if (arena[_adkey] is not ArenaData ad)
                return;

            ad.initLockship = false;
            ad.initSpec = false;

            if (!onlyArenaState)
            {
                LockWork(arena, false, notify, false, 0);
            }
        }

        bool IGame.HasLock(Arena arena)
        {
            if (arena == null || arena[_adkey] is not ArenaData ad)
                return false;

            return ad.initLockship;
        }

        void IGame.FakePosition(Player p, ref C2S_PositionPacket pos, int len)
        {
            HandlePositionPacket(p, ref pos, len, true);
        }

        void IGame.FakeKill(Player killer, Player killed, short pts, short flags)
        {
            NotifyKill(killer, killed, pts, flags, 0);
        }

        double IGame.GetIgnoreWeapons(Player p)
        {
            if (p == null)
                return 0;

            if (p[_pdkey] is not PlayerData pd)
                return 0;

            return pd.ignoreWeapons / (double)Constants.RandMax;
        }

        void IGame.SetIgnoreWeapons(Player p, double proportion)
        {
            if (p == null)
                return;

            if (p[_pdkey] is not PlayerData pd)
                return;

            pd.ignoreWeapons = (int)(Constants.RandMax * proportion);
        }

        void IGame.ShipReset(ITarget target)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            ReadOnlySpan<byte> shipResetBytes = stackalloc byte[1] { (byte)S2CPacketType.ShipReset };
            _net.SendToTarget(target, shipResetBytes, NetSendFlags.Reliable);

            _playerData.Lock();

            try
            {
                HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    _playerData.TargetToSet(target, set);

                    foreach (Player p in set)
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
                    _objectPoolManager.PlayerSetPool.Return(set);
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

            if (p[_pdkey] is not PlayerData pd)
                return;

            pd.wpnSent = (uint)(pd.wpnSent + packets);
        }

        void IGame.SetPlayerEnergyViewing(Player p, SeeEnergy value)
        {
            if (p == null)
                return;

            if (p[_pdkey] is not PlayerData pd)
                return;

            pd.pl_epd.seeNrg = value;
        }

        void IGame.SetSpectatorEnergyViewing(Player p, SeeEnergy value)
        {
            if (p == null)
                return;

            if (p[_pdkey] is not PlayerData pd)
                return;

            pd.pl_epd.seeNrgSpec = value;
        }

        void IGame.ResetPlayerEnergyViewing(Player p)
        {
            if (p == null)
                return;

            if (p[_pdkey] is not PlayerData pd)
                return;

            if (p.Arena[_adkey] is not ArenaData ad)
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

            if (p[_pdkey] is not PlayerData pd)
                return;

            if (p.Arena[_adkey] is not ArenaData ad)
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

        bool IGame.IsAntiwarped(Player p, HashSet<Player> playersAntiwarping)
        {
            if (p == null || p[_pdkey] is not PlayerData pd)
                return false;

            if (p.Arena[_adkey] is not ArenaData ad)
                return false;

            if (pd.MapRegionNoAnti)
                return false;

            bool antiwarped = false;
            
            _playerData.Lock();

            try
            {
                foreach (Player i in _playerData.PlayerList)
                {
                    if (p == null || p[_pdkey] is not PlayerData iData)
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

                            if (playersAntiwarping != null)
                            {
                                playersAntiwarping.Add(i);
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

        void IGame.Attach(Player p, Player to)
        {
            if (p == null
                || p.Status != PlayerState.Playing
                || p.Arena == null)
            {
                _logManager.LogM(LogLevel.Warn, nameof(Game), $"Failed to force attach player {p.Id} as a turret.");
                return;
            }

            if (to != null)
            {
                if (to == p
                    || to.Status != PlayerState.Playing
                    || to.Arena != p.Arena
                    || to.Freq != p.Freq)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(Game), $"Failed to force attach player {p.Id} as a turret onto player {to.Id}.");
                    return;
                }
            }

            Attach(p, to);
        }

        #endregion

        #region Persist methods

        private void Persist_GetShipLockData(Player player, Stream outStream)
        {
            if (player == null || player[_pdkey] is not PlayerData pd)
                return;

            lock (_freqshipmtx)
            {
                ExpireLock(player);

                if (pd.expires != null)
                {
                    SSProto.ShipLock protoShipLock = new();
                    protoShipLock.Expires = Timestamp.FromDateTime(pd.expires.Value);

                    protoShipLock.WriteTo(outStream);
                }
            }
        }

        private void Persist_SetShipLockData(Player player, Stream inStream)
        {
            if (player == null || player[_pdkey] is not PlayerData pd)
                return;

            lock (_freqshipmtx)
            {
                SSProto.ShipLock protoShipLock = SSProto.ShipLock.Parser.ParseFrom(inStream);
                pd.expires = protoShipLock.Expires.ToDateTime();
                pd.lockship = true;

                // Try expiring once now, and...
                ExpireLock(player);

                // If the lock is still active, force to spec.
                if (pd.lockship)
                {
                    player.Ship = ShipType.Spec;
                    player.Freq = player.Arena.SpecFreq;
                }
            }
        }

        #endregion

        [ConfigHelp("Misc", "RegionCheckInterval", ConfigScope.Arena, typeof(int), DefaultValue = "100", 
            Description = "How often to check for region enter/exit events (in ticks).")]
        [ConfigHelp("Misc", "SpecSeeExtra", ConfigScope.Arena, typeof(bool), DefaultValue = "1", 
            Description = "Whether spectators can see extra data for the person they're spectating.")]
        [ConfigHelp("Misc", "SpecSeeEnergy", ConfigScope.Arena, typeof(SeeEnergy), DefaultValue = "All", 
            Description = "Whose energy levels spectators can see. The options are the same as for Misc:SeeEnergy, with one addition: 'Spec' means only the player you're spectating.")]
        [ConfigHelp("Misc", "SeeEnergy", ConfigScope.Arena, typeof(SeeEnergy), DefaultValue = "None", 
            Description = "Whose energy levels everyone can see: 'None' means nobody else's, 'All' is everyone's, 'Team' is only teammates.")]
        [ConfigHelp("Security", "MaxDeathWithoutFiring", ConfigScope.Arena, typeof(int), DefaultValue = "5", 
            Description = "The number of times a player can die without firing a weapon before being placed in spectator mode.")]
        [ConfigHelp("Misc", "NoSafeAntiwarp", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
            Description = "Disables antiwarp on players in safe zones.")]
        [ConfigHelp("Misc", "WarpTresholdDelta", ConfigScope.Arena, typeof(int), DefaultValue = "320", 
            Description = "The amount of change in a players position (in pixels) that is considered a warp (only while he is flashing).")]
        [ConfigHelp("Prize", "DontShareThor", ConfigScope.Arena, typeof(bool), DefaultValue = "0", 
            Description = "Whether Thor greens don't go to the whole team.")]
        [ConfigHelp("Prize", "DontShareBurst", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
            Description = "Whether Burst greens don't go to the whole team.")]
        [ConfigHelp("Prize", "DontShareBrick", ConfigScope.Arena, typeof(bool), DefaultValue = "0",
            Description = "Whether Brick greens don't go to the whole team.")]
        [ConfigHelp("Net", "BulletPixels", ConfigScope.Arena, typeof(int), DefaultValue = "1500", 
            Description = "How far away to always send bullets (in pixels).")]
        [ConfigHelp("Net", "WeaponPixels", ConfigScope.Arena, typeof(int), DefaultValue = "2000",
            Description = "How far away to always weapons (in pixels).")]
        [ConfigHelp("Net", "PositionExtraPixels", ConfigScope.Arena, typeof(int), DefaultValue = "8000",
            Description = "How far away to to send positions of players on radar (in pixels).")]
        [ConfigHelp("Net", "AntiwarpSendPercent", ConfigScope.Arena, typeof(int), DefaultValue = "5", 
            Description = "Percent of position packets with antiwarp enabled to send to the whole arena.")]
        // Note: Toggle:AntiwarpPixels is a client setting, so it's [ConfigHelp] is in ClientSettingsConfig.cs
        // Note: Kill:EnterDelay is a client setting, so it's [ConfigHelp] is in ClientSettingsConfig.cs
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null)
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ArenaData ad = arena[_adkey] as ArenaData;

                ad.RegionCheckTime = TimeSpan.FromMilliseconds(_configManager.GetInt(arena.Cfg, "Misc", "RegionCheckInterval", 100) * 10);

                ad.SpecSeeExtra = _configManager.GetInt(arena.Cfg, "Misc", "SpecSeeExtra", 1) != 0;

                ad.SpecSeeEnergy = _configManager.GetEnum(arena.Cfg, "Misc", "SpecSeeEnergy", SeeEnergy.All);

                ad.AllSeeEnergy = _configManager.GetEnum(arena.Cfg, "Misc", "SeeEnergy", SeeEnergy.None);

                ad.MaxDeathWithoutFiring = _configManager.GetInt(arena.Cfg, "Security", "MaxDeathWithoutFiring", 5);

                ad.NoSafeAntiwarp = _configManager.GetInt(arena.Cfg, "Misc", "NoSafeAntiwarp", 0) != 0;

                ad.WarpThresholdDelta = _configManager.GetInt(arena.Cfg, "Misc", "WarpTresholdDelta", 320);
                ad.WarpThresholdDelta *= ad.WarpThresholdDelta; // TODO: figure out why it's the value squared

                PersonalGreen pg = PersonalGreen.None;

                if (_configManager.GetInt(arena.Cfg, "Prize", "DontShareThor", 0) != 0)
                    pg |= PersonalGreen.Thor;

                if (_configManager.GetInt(arena.Cfg, "Prize", "DontShareBurst", 0) != 0)
                    pg |= PersonalGreen.Burst;

                if (_configManager.GetInt(arena.Cfg, "Prize", "DontShareBrick", 0) != 0)
                    pg |= PersonalGreen.Brick;

                ad.personalGreen = pg;

                int cfg_bulletpix = _configManager.GetInt(_configManager.Global, "Net", "BulletPixels", 1500);

                int cfg_wpnpix = _configManager.GetInt(_configManager.Global, "Net", "WeaponPixels", 2000);

                ad.cfg_pospix = _configManager.GetInt(_configManager.Global, "Net", "PositionExtraPixels", 8000);

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

            if (p[_pdkey] is not PlayerData pd)
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

                pd.LastRegionSet = ImmutableHashSet<MapRegion>.Empty;

                pd.lockship = ad.initLockship;
                if (ad.initSpec)
                {
                    p.Ship = ShipType.Spec;
                    p.Freq = (short)arena.SpecFreq;
                }

                p.Attached = -1;

                pd.PlayerPostitionPacket_LastShip = null;

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
                            if (i[_pdkey] is not PlayerData idata)
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
                        _logManager.LogP(LogLevel.Error, nameof(Game), p, "Extra position data queries is still nonzero.");

                    ClearSpeccing(pd);
                }

                pd.LastRegionSet = ImmutableHashSet<MapRegion>.Empty;
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
                            if (i[_pdkey] is not PlayerData idata)
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
                        if (data.speccing[_pdkey] is not PlayerData odata)
                            return;

                        if (--odata.epdQueries <= 0)
                        {
                            // no more people speccing the player that want extra position data
                            SendSpecBytes(data.speccing, false);
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
                    if (t[_pdkey] is not PlayerData tdata)
                        return;

                    if (tdata.epdQueries++ == 0)
                    {
                        // first time player is being specced by someone watching extra position data, tell the player to send extra position data
                        SendSpecBytes(t, true);
                    }
                }
            }
        }

        private void SendSpecBytes(Player t, bool sendExtraPositionData)
        {
            if (t == null)
                return;

            ReadOnlySpan<byte> specBytes = stackalloc byte[2] { (byte)S2CPacketType.SpecData, sendExtraPositionData ? (byte)1 : (byte)0 };
            _net.SendToOne(t, specBytes, NetSendFlags.Reliable);
        }

        private void Packet_Position(Player p, byte[] data, int len)
        {
            ref C2S_PositionPacket pos = ref MemoryMarshal.AsRef<C2S_PositionPacket>(new Span<byte>(data, 0, C2S_PositionPacket.LengthWithExtra));
            HandlePositionPacket(p, ref pos, len, false);
        }

        private void HandlePositionPacket(Player p, ref C2S_PositionPacket pos, int len, bool isFake)
        {
            if (p == null || p.Status != PlayerState.Playing)
                return;

#if CFG_RELAX_LENGTH_CHECKS
            if(len < C2SPositionPacket.Length)
#else
            if (len != C2S_PositionPacket.Length && len != C2S_PositionPacket.LengthWithExtra)
#endif
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, $"Bad position packet len={len}.");
                return;
            }

            Arena arena = p.Arena;
            if (arena == null || arena.Status != ArenaState.Running)
                return;

            // Verify checksum
            if (!isFake && !pos.IsValidChecksum)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "Bad position packet checksum.");
                return;
            }

            if (pos.X == -1 && pos.Y == -1)
            {
                // position sent after death, before respawn. these aren't
                // really useful for anything except making sure the server
                // knows the client hasn't dropped, so just ignore them.
                return;
            }

            if (p[_pdkey] is not PlayerData pd)
                return;

            if (arena[_adkey] is not ArenaData ad)
                return;

            DateTime now = DateTime.UtcNow;
            ServerTick gtc = ServerTick.Now;

            // lag data
            if (_lagCollect != null && !isFake)
            {
                _lagCollect.Position(
                    p,
                    (gtc - pos.Time) * 10,
                    len >= 26 ? pos.Extra.S2CPing * 10 : new int?(),
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
                pd.pos = pos;

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

            int randnum = _prng.Rand();

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
                if ((_prng.Rand() < pd.ignoreWeapons) 
                    || pd.MapRegionNoWeapons)
                {
                    pos.Weapon.Type = WeaponCodes.Null;
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
                if (!isNewer && !isFake && pos.Weapon.Type == WeaponCodes.Null)
                    return;

                // Consult the player position advisors to allow other modules to edit the packet.
                var advisors = arena.GetAdvisors<IPlayerPositionAdvisor>();
                foreach (var advisor in advisors)
                {
                    advisor.EditPositionPacket(p, ref pos);

                    // Allow advisors to drop the position packet.
                    if (pos.X < 0 || pos.Y < 0) // slightly different than ASSS, here we consider anything negative to mean drop whereas ASSS looks for -1
                        return;
                }

                // by default, send unreliable droppable packets. 
                // weapons get a higher priority.
                NetSendFlags nflags = NetSendFlags.Unreliable | NetSendFlags.Droppable | 
                    (pos.Weapon.Type != WeaponCodes.Null ? NetSendFlags.PriorityP5 : NetSendFlags.PriorityP3);

                // there are several reasons to send a weapon packet (05) instead of just a position one (28)
                bool sendWeapon = (
                    (pos.Weapon.Type != WeaponCodes.Null) // a real weapon
                    || (pos.Bounty > byte.MaxValue) // bounty over 255
                    || (p.Id > byte.MaxValue)); //pid over 255

                // TODO: for arenas designed for a small # of players (e.g. 4v4 league), a way to always send to all? or is boosting weapon range settings to max good enough?
                bool sendToAll = false;

                // TODO: if a player is far away from the mine, how would that player know if the mine was cleared (detonated by another player outside of their range or repelled)?
                // would need a module to keep track of mine locations and do pseudo-region like comparisons? and use that know when to send a position packet to all?
                // what about bombs fired at low velocity? need wall collision detection?
                // TODO: a way for the server to keep track of where mines are currently placed so that it can replay packets to players that enter the arena? or does something like this exist already?

                // send mines to everyone
                if ((pos.Weapon.Type == WeaponCodes.Bomb || pos.Weapon.Type == WeaponCodes.ProxBomb) 
                    && pos.Weapon.Alternate)
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
                if ((pos.Weapon.Type == WeaponCodes.Null)
                    && ((pos.Status & PlayerPositionStatus.Antiwarp) == PlayerPositionStatus.Antiwarp)
                    && (_prng.Rand() < ad.cfg_sendanti))
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

                C2S_PositionPacket copy = new();
                S2C_WeaponsPacket wpn = new();
                S2C_PositionPacket sendpos = new();

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
                        if (i[_pdkey] is not PlayerData idata)
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

                                if (i.Ship == ShipType.Spec)
                                {
                                    if (idata.pl_epd.seeEpd && idata.speccing == p)
                                    {
                                        extralen = (len >= C2S_PositionPacket.LengthWithExtra) ? ExtraPositionData.Length : nrglen;
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
                                    copy = pos;
                                    modified = false;
                                }

                                bool drop = false;

                                // Consult the advisors to allow other modules to edit the packet going to player i.
                                foreach (var advisor in advisors)
                                {
                                    if (advisor.EditIndividualPositionPacket(p, i, ref copy, ref extralen))
                                        modified = true;

                                    // Allow advisors to drop the packet.
                                    if (copy.X < 0 || copy.Y < 0) // slightly different than ASSS, here we consider anything negative to mean drop whereas ASSS looks for -1
                                    {
                                        drop = true;
                                        break;
                                    }
                                }

                                wpnDirty = wpnDirty || modified;
                                posDirty = posDirty || modified;

                                if (!drop)
                                {
                                    if ((!modified && sendWeapon)
                                        || copy.Weapon.Type > 0
                                        || (copy.Bounty & 0xFF00) != 0
                                        || (p.Id & 0xFF00) != 0)
                                    {
                                        int length = S2C_WeaponsPacket.Length + extralen;

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
                                            wpn.Weapon = copy.Weapon;
                                            wpn.Extra = copy.Extra;

                                            // move this field from the main packet to the extra data, in case they don't match.
                                            wpn.Extra.Energy = (ushort)copy.Energy;

                                            wpnDirty = modified;

                                            wpn.SetChecksum();
                                        }

                                        if (wpn.Weapon.Type != 0)
                                            idata.wpnSent++;

                                        ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref wpn, 1));
                                        if (data.Length > length)
                                            data = data.Slice(0, length);

                                        _net.SendToOne(i, data, nflags);
                                    }
                                    else
                                    {
                                        int length = S2C_PositionPacket.Length + extralen;

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
                                            sendpos.Extra.Energy = (ushort)copy.Energy;

                                            posDirty = modified;
                                        }

                                        ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref sendpos, 1));
                                        if (data.Length > length)
                                            data = data.Slice(0, length);

                                        _net.SendToOne(i, data, nflags);
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

                PlayerPositionPacketCallback.Fire(arena, p, in pos);
            }

            pd.PlayerPostitionPacket_LastShip = p.Ship;
        }

        private void UpdateRegions(Player p, short x, short y)
        {
            if (p == null)
                return;

            if (p[_pdkey] is not PlayerData pd)
                return;

            Arena arena = p.Arena;
            IImmutableSet<MapRegion> oldRegions = pd.LastRegionSet;
            IImmutableSet<MapRegion> newRegions = _mapData.RegionsAt(p.Arena, x, y);

            pd.MapRegionNoAnti = pd.MapRegionNoWeapons = false;

            foreach (MapRegion region in newRegions)
            {
                if (region.NoAntiwarp)
                    pd.MapRegionNoAnti = true;

                if (region.NoWeapons)
                    pd.MapRegionNoWeapons = true;

                if (!oldRegions.Contains(region))
                {
                    MapRegionCallback.Fire(arena, p, region, x, y, true); // entered region
                }
            }

            foreach (MapRegion region in oldRegions)
            {
                if (!newRegions.Contains(region))
                {
                    MapRegionCallback.Fire(arena, p, region, x, y, false); // exited region
                }
            }

            pd.LastRegionSet = newRegions;
        }

        private void Packet_SpecRequest(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len != C2S_SpecRequest.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, $"Bad spec req packet len={len}.");
                return;
            }

            if (p.Status != PlayerState.Playing || p.Ship != ShipType.Spec)
                return;

            if (p[_pdkey] is not PlayerData pd)
                return;

            ref C2S_SpecRequest packet = ref MemoryMarshal.AsRef<C2S_SpecRequest>(data.AsSpan(0, C2S_SpecRequest.Length));
            int targetPlayerId = packet.PlayerId;

            lock (_specmtx)
            {
                ClearSpeccing(pd);

                if (targetPlayerId >= 0)
                {
                    Player t = _playerData.PidToPlayer(targetPlayerId);
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
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, $"Bad ship req packet len={len}.");
                return;
            }

            Arena arena = p.Arena;
            if (p.Status != PlayerState.Playing || arena == null)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Game), p, "State sync problem: Ship request from bad status.");
                return;
            }

            if (p[_pdkey] is not PlayerData pd)
                return;

            ShipType ship = (ShipType)data[1];
            if (ship < ShipType.Warbird || ship > ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, $"Bad ship number: {ship}.");
                return;
            }

            short freq = p.Freq;

            lock (_freqshipmtx)
            {
                if (p.Flags.DuringChange)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Game), p, "State sync problem: Ship request before ack from previous change.");
                    return;
                }

                if (ship == p.Ship)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(Game), p, "State sync problem: Already in requested ship.");
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
                    _chat.SendMessage(p, $"You have been locked in {(p.Ship == ShipType.Spec ? "spectator mode" : "your ship")}.");

                return;
            }

            
            IFreqManager fm = _broker.GetInterface<IFreqManager>();
            if (fm != null)
            {
                try
                {
                    StringBuilder errorBuilder = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        fm.ShipChange(p, ship, errorBuilder);

                        if (errorBuilder.Length > 0)
                        {
                            _chat.SendMessage(p, errorBuilder);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(errorBuilder);
                    }
                }
                finally
                {
                    _broker.ReleaseInterface(ref fm);
                }
            }
            else
            {
                SetShipAndFreq(p, ship, freq);
            }
        }

        private void Packet_SetFreq(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (len != C2S_SetFreq.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, $"Bad freq req packet len={len}.");
            }
            else if (p.Flags.DuringChange)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Game), p, "State sync problem: Freq change before ack from previous change.");
            }
            else
            {
                ref C2S_SetFreq packet = ref MemoryMarshal.AsRef<C2S_SetFreq>(data.AsSpan(0, C2S_SetFreq.Length));
                FreqChangeRequest(p, packet.Freq);
            }
        }

        private void FreqChangeRequest(Player p, short freq)
        {
            if(p == null)
                return;

            if (p[_pdkey] is not PlayerData pd)
                return;

            Arena arena = p.Arena;
            ShipType ship = p.Ship;

            if (p.Status != PlayerState.Playing || arena == null)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, "Freq change from bad arena.");
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
                    _chat.SendMessage(p, $"You have been locked in {(p.Ship == ShipType.Spec ? "spectator mode" : "your ship")}.");

                return;
            }

            IFreqManager fm = _broker.GetInterface<IFreqManager>();
            if (fm != null)
            {
                try
                {
                    StringBuilder errorBuilder = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        fm.FreqChange(p, freq, errorBuilder);

                        if (errorBuilder.Length > 0)
                        {
                            _chat.SendMessage(p, errorBuilder);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(errorBuilder);
                    }
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

            if (p[_pdkey] is not PlayerData pd)
                return;

            ShipType oldShip = p.Ship;
            short oldFreq = p.Freq;
            SpawnCallback.SpawnReason flags = SpawnCallback.SpawnReason.ShipChange;

            if (p.Type == ClientType.Chat && ship != ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Warn, nameof(Game), p, "Attempted to force a chat client into a playing ship.");
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

            S2C_ShipChange packet = new((sbyte)ship, (short)p.Id, freq);
            ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1));


            if (p.IsStandard)
            {
                // send it to him, with a callback
                _net.SendWithCallback(p, data, ResetDuringChange);
            }

            // send it to everyone else
            _net.SendToArena(arena, p, data, NetSendFlags.Reliable);

            //if(_chatnet != null)


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

            _logManager.LogP(LogLevel.Info, nameof(Game), p, $"Changed ship/freq to ship {ship}, freq {freq}.");
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

            S2C_FreqChange packet = new((short)p.Id, freq);
            ReadOnlySpan<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1));

            // him with callback
            if (p.IsStandard)
                _net.SendWithCallback(p, data, ResetDuringChange);
                
            // everyone else
            _net.SendToArena(arena, p, data, NetSendFlags.Reliable);

            //if(_chatNet != null)

            PreShipFreqChangeCallback.Fire(arena, p, p.Ship, p.Ship, freq, oldFreq);
            DoShipFreqChangeCallback(p, p.Ship, p.Ship, freq, oldFreq);

            _logManager.LogP(LogLevel.Info, nameof(Game), p, $"Changed freq to {freq}.");
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

            if (p[_pdkey] is not PlayerData pd)
                return;

            lock (_freqshipmtx)
            {
                if(pd.expires != null)
                    if (DateTime.UtcNow > pd.expires)
                    {
                        pd.lockship = false;
                        pd.expires = null;
                        _logManager.LogP(LogLevel.Drivel, nameof(Game), p, "Lock expired.");
                    }
            }
        }
        
        [ConfigHelp("Prize", "UseTeamkillPrize", ConfigScope.Arena, typeof(bool), DefaultValue = "0", 
            Description = "Whether to use a special prize for teamkills. Prize:TeamkillPrize specifies the prize #.")]
        [ConfigHelp("Prize", "TeamkillPrize", ConfigScope.Arena, typeof(int), DefaultValue = "0",
            Description = "The prize # to give for a teamkill, if Prize:UseTeamkillPrize=1.")]
        private void Packet_Die(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len != C2S_Die.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, $"Bad death packet len={len}.");
                return;
            }

            if (p.Status != PlayerState.Playing)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            if (arena[_adkey] is not ArenaData ad)
                return;

            ref C2S_Die packet = ref MemoryMarshal.AsRef<C2S_Die>(data.AsSpan(0, C2S_Die.Length));
            short bty = packet.Bounty;

            Player killer = _playerData.PidToPlayer(packet.Killer);
            if (killer == null || killer.Status != PlayerState.Playing || killer.Arena != arena)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, $"Reported kill by bad pid {packet.Killer}.");
                return;
            }

            short flagCount = p.Packet.FlagsCarried;

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

            var killAdvisors = arena.GetAdvisors<IKillAdvisor>();

            // Consult the advisors after setting the above flags, the flags reflect the real state of the player.
            foreach (var advisor in killAdvisors)
            {
                advisor.EditDeath(arena, ref killer, ref p, ref bty);

                if (p == null || killer == null)
                    return; // The advisor wants to drop the kill packet.

                if (p.Status != PlayerState.Playing || p.Arena != arena)
                {
                    _logManager.LogP(LogLevel.Error, nameof(Game), p, $"An {nameof(IKillAdvisor)} set killed to a bad player.");
                    return;
                }

                if (killer.Status != PlayerState.Playing || killer.Arena != arena)
                {
                    _logManager.LogP(LogLevel.Error, nameof(Game), killer, $"An {nameof(IKillAdvisor)} set killer to a bad player.");
                    return;
                }
            }

            // Pick the green.
            Prize green;
            if ((p.Freq == killer.Freq) && (_configManager.GetInt(arena.Cfg, "Prize", "UseTeamkillPrize", 0) != 0))
            {
                green = (Prize)_configManager.GetInt(arena.Cfg, "Prize", "TeamkillPrize", 0);
            }
            else
            {
                // Pick a random green.
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

            // Use advsiors to determine how many points to award.
            short pts = 0;
            foreach (var advisor in killAdvisors)
            {
                pts += advisor.KillPoints(arena, killer, p, bty, flagCount);
            }

            // Allow a module to modify the green sent in the packet.
            IKillGreen killGreen = arena.GetInterface<IKillGreen>();
            if (killGreen != null)
            {
                try
                {
                    killGreen.KillGreen(arena, killer, p, bty, flagCount, pts, green);
                }
                finally
                {
                    arena.ReleaseInterface(ref killGreen);
                }
            }

            // Record the kill points on our side.
            if (pts > 0)
            {
                IAllPlayerStats allPlayerStats = _broker.GetInterface<IAllPlayerStats>();
                if (allPlayerStats != null)
                {
                    try
                    {
                        allPlayerStats.IncrementStat(killer, StatCodes.KillPoints, null, (ulong)pts);
                    }
                    finally
                    {
                        _broker.ReleaseInterface(ref allPlayerStats);
                    }
                }
            }

            NotifyKill(killer, p, pts, flagCount, green);

            FireKillEvent(arena, killer, p, bty, flagCount, pts, green);

            _logManager.LogA(LogLevel.Info, nameof(Game), arena, $"{p.Name} killed by {killer.Name} (bty={bty},flags={flagCount},pts={pts})");

            if (!p.Flags.SentWeaponPacket)
            {
                if (p[_pdkey] is PlayerData pd)
                {
                    if (pd.deathWithoutFiring++ == ad.MaxDeathWithoutFiring)
                    {
                        _logManager.LogP(LogLevel.Info, nameof(Game), p, "Specced for too many deaths without firing.");
                        SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);
                    }
                }
            }

            // reset this so we can accurately check deaths without firing
            p.Flags.SentWeaponPacket = false;
        }

        private static void FireKillEvent(Arena arena, Player killer, Player killed, short bty, short flagCount, short pts, Prize green)
        {
            if (arena == null || killer == null || killed == null)
                return;

            KillCallback.Fire(arena, arena, killer, killed, bty, flagCount, pts, green);
        }

        private void NotifyKill(Player killer, Player killed, short pts, short flagCount, Prize green)
        {
            if (killer == null || killed == null)
                return;

            S2C_Kill packet = new(green, (short)killer.Id, (short)killed.Id, pts, flagCount);
            _net.SendToArena(killer.Arena, null, ref packet, NetSendFlags.Reliable);

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
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, $"Bad green packet len={len}.");
                return;
            }

            if (p.Status != PlayerState.Playing)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            if (arena[_adkey] is not ArenaData ad)
                return;

            ref GreenPacket g = ref MemoryMarshal.AsRef<GreenPacket>(data);
            Prize prize = g.Green;

            // don't forward non-shared prizes
            if(!(prize == Prize.Thor && (ad.personalGreen & PersonalGreen.Thor) == PersonalGreen.Thor) &&
                !(prize == Prize.Burst && (ad.personalGreen & PersonalGreen.Burst) == PersonalGreen.Burst) &&
                !(prize == Prize.Brick && (ad.personalGreen & PersonalGreen.Brick) == PersonalGreen.Brick))
            {
                g.PlayerId = (short)p.Id;
                g.Type = (byte)S2CPacketType.Green; // HACK: reuse the buffer that it came in on
                _net.SendToArena(arena, p, new ReadOnlySpan<byte>(data, 0, GreenPacket.S2CLength), NetSendFlags.Unreliable);
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

        // Note: This method assumes all validation checks have been done beforehand (playing, same arena, same team, etc...).
        private void Attach(Player p, Player to)
        {
            if (p == null)
                return;

            short toPlayerId = (short)(to != null ? to.Id : -1);

            // only if state has changed
            if (p.Attached != toPlayerId)
            {
                // Send the packet
                S2C_Turret packet = new((short)p.Id, toPlayerId);
                _net.SendToArena(p.Arena, null, ref packet, NetSendFlags.Reliable);

                // Update the state
                p.Attached = toPlayerId;

                // Invoke the callback
                // TODO: ASSS has does the callback synchronously for incoming packets, and has main run it for force attach.  figure out what to do
                AttachCallback.Fire(p.Arena, p, to);
            }
        }

        private void Packet_AttachTo(Player p, byte[] data, int len)
        {
            if (p == null || data == null)
                return;

            if (len != C2S_AttachTo.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Game), p, $"Bad attach req packet len={len}.");
                return;
            }

            if (p.Status != PlayerState.Playing)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            ref C2S_AttachTo packet = ref MemoryMarshal.AsRef<C2S_AttachTo>(data.AsSpan(0, C2S_AttachTo.Length));
            short pid2 = packet.PlayerId;

            Player to = null;

            // -1 means detaching
            if (pid2 != -1)
            {
                to = _playerData.PidToPlayer(pid2);

                if (to == null
                    || to == p
                    || to.Status != PlayerState.Playing
                    || p.Arena != to.Arena 
                    || p.Freq != to.Freq)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(Game), p, $"Tried to attach to bad pid {pid2}.");
                    return;
                }
            }

            Attach(p, to);
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

            S2C_TurretKickoff packet = new((short)p.Id);
            _net.SendToArena(arena, null, ref packet, NetSendFlags.Reliable);
        }

        private static int Hypot(int dx, int dy)
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

        private void LockWork(ITarget target, bool nval, bool notify, bool spec, int timeout)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.TargetToSet(target, set);

                foreach (Player p in set)
                {
                    if (p[_pdkey] is not PlayerData pd)
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
                    if (nval == false || timeout == 0)
                        pd.expires = null;
                    else
                        pd.expires = DateTime.UtcNow.AddSeconds(timeout);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = null,
            Description =
            "Displays players spectating you. When private, displays players\n" +
            "spectating the target.")]
        private void Command_spec(string command, string parameters, Player p, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
                targetPlayer = p;

            int specCount = 0;

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                _playerData.Lock();

                try
                {
                    foreach (Player playerToCheck in _playerData.PlayerList)
                    {
                        if (playerToCheck[_pdkey] is not PlayerData pd)
                            continue;

                        if (pd.speccing == targetPlayer
                            && (!_capabilityManager.HasCapability(playerToCheck, Constants.Capabilities.InvisibleSpectator)
                                || _capabilityManager.HigherThan(p, playerToCheck)))
                        {
                            specCount++;

                            if (sb.Length > 0)
                                sb.Append(", ");

                            sb.Append(playerToCheck.Name);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                if (specCount > 1)
                {
                    _chat.SendMessage(p, $"{specCount} spectators: ");
                    _chat.SendWrappedText(p, sb);
                }
                else if (specCount == 1)
                {
                    _chat.SendMessage(p, $"1 spectator: {sb}");
                }
                else if (p == targetPlayer)
                {
                    _chat.SendMessage(p, "No players are spectating you.");
                }
                else
                {
                    _chat.SendMessage(p, $"No players are spectating {targetPlayer.Name}.");
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
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
            target.TryGetPlayerTarget(out Player targetPlayer);

            SeeEnergy nval = SeeEnergy.All;
            bool spec = false;

            if (!string.IsNullOrEmpty(parameters) && parameters.Contains("-t"))
                nval = SeeEnergy.Team;

            if (!string.IsNullOrEmpty(parameters) && parameters.Contains("-n"))
                nval = SeeEnergy.None;

            if (!string.IsNullOrEmpty(parameters) && parameters.Contains("-s"))
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
                if (p.Arena[_adkey] is not ArenaData ad)
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
                mainloopWork_RunSpawnCallbackAction,
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
                mainloopWork_RunShipFreqChangeCallbackAction,
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

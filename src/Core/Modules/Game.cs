using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using SS.Utilities;
using SS.Core.ComponentCallbacks;

namespace SS.Core.Modules
{
    public class Game : IModule, IGame
    {
        private ModuleManager _mm;
        private IPlayerData _playerData;
        private IConfigManager _configManager;
        private IServerTimer _mainLoop;
        private ILogManager _logManager;
        private INetwork _net;
        //private IChatNet _chatnet;
        private IArenaManagerCore _arenaManager;
        private ICapabilityManager _capabilityManager;
        //private IMapData _mapData;
        //private ILagCollect _lagCollect;
        private IChat _chat;
        //private ICommandManager _commandManager;
        //private IPersist _persist;

        private int _pdkey;
        private int _adkey;

        private int cfg_bulletpix, cfg_wpnpix, cfg_pospix;
        private int cfg_sendanti, cfg_changelimit;
        private int[] wpnRange = new int[WeaponCount]; // these are bit positions for the personalgreen field

        private object _specmtx = new object();
        private object _freqshipmtx = new object();

        private const int WeaponCount = 32;

        private static readonly byte[] _addSpecBytes = new byte[] { (byte)S2CPacketType.SpecData, 1 };
        private static readonly byte[] _clearSpecBytes = new byte[] { (byte)S2CPacketType.SpecData, 0 };
        private static readonly byte[] _shipResetBytes = new byte[] { (byte)S2CPacketType.ShipReset };

        private Random random = new Random();

        [Flags]
        private enum PersonalGreen
        {
            None = 0, 
            Thor = 1, 
            Burst = 2, 
            Brick = 4, 
        }

        private enum SeeEnergy
        {
            /// <summary>
            /// Nobody can see energy
            /// </summary>
            None,

            /// <summary>
            /// everyone can see everyone's
            /// </summary>
            All, 

            /// <summary>
            /// you can see only energy for teammates
            /// </summary>
            Team, 

            /// <summary>
            /// can see energy/extra data only for who you are speccing
            /// </summary>
            Spec, 
        }

        private class PlayerData
        {
            public C2SPositionPacket pos = new C2SPositionPacket(new byte[C2SPositionPacket.LengthWithExtra]);
            public Player speccing;
            public uint wpnSent;
            public int ignoreWeapons, deathWithoutFiring;

            public struct ShipChangeTracking
            {
                public int changes;
                public DateTime lastCheck;

                public void doExponentialDecay()
                {
                    // exponential decay by 1/2 every 10 seconds
                    int seconds = (int)((DateTime.Now - lastCheck).TotalSeconds);
                    if (seconds > 31)
                        changes = 0;
                    else if (seconds > 0)
                        changes >>= seconds;

                    lastCheck = lastCheck.AddSeconds(seconds);
                }
            }

            public ShipChangeTracking changes;

            // extra position data/energy stuff
            public int epdQueries;

            public struct PlExtraPositionData
            {
                public SeeEnergy seeNrg;
                public SeeEnergy seeNrgSpec;
                public SeeEnergy seePd;
            }
            public PlExtraPositionData pl_epd;

            // some flags
            public bool lockship;
            public bool rgnnoanti, rgnnoweapons;

            /// <summary>
            /// when the lock expires, or null for session-long lock
            /// </summary>
            public DateTime? expires;

            /// <summary>
            /// when we last updated the region-based flags
            /// </summary>
            public DateTime lastRgnCheck;

            public LinkedList<object> lastrgnset = new LinkedList<object>();
        }

        private class ArenaData
        {
            public bool spec_epd;
            public SeeEnergy specNrg;
            public SeeEnergy allNrg;
            public PersonalGreen personalGreen;
            public bool initLockship;
            public bool initSpec;
            public int deathWithoutFiring;
            public int regionCheckTime;
        }

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get
            {
                return new Type[] {
                    typeof(IPlayerData), 
                    typeof(IConfigManager),
                    typeof(IServerTimer),
                    typeof(ILogManager),
                    typeof(INetwork),
                    //typeof(IChatNet),
                    typeof(IArenaManagerCore),
                    typeof(ICapabilityManager),
                    //typeof(IMapData),
                    //typeof(ILagCollect),
                    typeof(IChat),
                    //typeof(ICommandManager),
                    //typeof(IPersist)
                };
            }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;
            _playerData = interfaceDependencies[typeof(IPlayerData)] as IPlayerData;
            _configManager = interfaceDependencies[typeof(IConfigManager)] as IConfigManager;
            _mainLoop = interfaceDependencies[typeof(IServerTimer)] as IServerTimer;
            _logManager = interfaceDependencies[typeof(ILogManager)] as ILogManager;
            _net = interfaceDependencies[typeof(INetwork)] as INetwork;
            //_chatnet = interfaceDependencies[typeof(IChatNet)] as IChatNet;
            _arenaManager = interfaceDependencies[typeof(IArenaManagerCore)] as IArenaManagerCore;
            _capabilityManager = interfaceDependencies[typeof(ICapabilityManager)] as ICapabilityManager;
            //_mapData = interfaceDependencies[typeof(IMapData)] as IMapData;
            //_lagCollect = interfaceDependencies[typeof(ILagCollect)] as ILagCollect;
            _chat = interfaceDependencies[typeof(IChat)] as IChat;
            //_commandManager = interfaceDependencies[typeof(ICommandManager)] as ICommandManager;
            //_persist = interfaceDependencies[typeof(IPersist)] as IPersist;

            _adkey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdkey = _playerData.AllocatePlayerData<PlayerData>();
            
            //if(_persist != null)
                //_persist.

            // How far away to always send bullets (in pixels).
            cfg_bulletpix = _configManager.GetInt(_configManager.Global, "Net", "BulletPixels", 1500);

            // How far away to always send weapons (in pixels).
            cfg_wpnpix = _configManager.GetInt(_configManager.Global, "Net", "WeaponPixels", 2000);

            // How far away to send positions of players on radar.
            cfg_pospix = _configManager.GetInt(_configManager.Global, "Net", "PositionExtraPixels", 8000);

            // Percent of position packets with antiwarp enabled to send to the whole arena.
            cfg_sendanti = _configManager.GetInt(_configManager.Global, "Net", "AntiwarpSendPercent", 5);
            cfg_sendanti = 32767 / 100 * cfg_sendanti; // TODO: figure out why he is doing this!

            // The number of ship changes in a short time (about 10 seconds) before ship changing is disabled (for about 30 seconds).
            cfg_changelimit = _configManager.GetInt(_configManager.Global, "General", "ShipChangeLimit", 10);

            for (int x = 0; x < wpnRange.Length; x++)
                wpnRange[x] = cfg_wpnpix;

            // exceptions
            wpnRange[(int)WeaponCodes.Bullet] = cfg_bulletpix;
            wpnRange[(int)WeaponCodes.BounceBullet] = cfg_bulletpix;
            wpnRange[(int)WeaponCodes.Thor] = 30000;

            ArenaActionCallback.Register(_mm, arenaAction);
            PlayerActionCallback.Register(_mm, playerAction);
            NewPlayerCallback.Register(_mm, newPlayer);

            _net.AddPacket((int)C2SPacketType.Position, new PacketDelegate(recievedPositionPacket));
            _net.AddPacket((int)C2SPacketType.SpecRequest, new PacketDelegate(recievedSpecRequestPacket));
            _net.AddPacket((int)C2SPacketType.SetShip, new PacketDelegate(recievedSetShipPacket));
            _net.AddPacket((int)C2SPacketType.SetFreq, new PacketDelegate(recievedSetFreqPacket));
            _net.AddPacket((int)C2SPacketType.Die, new PacketDelegate(recievedDiePacket));
            _net.AddPacket((int)C2SPacketType.Green, new PacketDelegate(recievedGreenPacket));
            _net.AddPacket((int)C2SPacketType.AttachTo, new PacketDelegate(recievedAttachToPacket));
            _net.AddPacket((int)C2SPacketType.TurretKickOff, new PacketDelegate(recievedTurretKickoffPacket));

            //if(_chatnet != null)
                //_chatnet.

            //if (_commandManager != null)
            //{
                //_commandManager.
            //}

            _mm.RegisterInterface<IGame>(this);

            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            _mm.UnregisterInterface<IGame>();

            //if(_chatnet != null)

            //if(_commandManager != null)

            _net.RemovePacket((int)C2SPacketType.Position, new PacketDelegate(recievedPositionPacket));
            _net.RemovePacket((int)C2SPacketType.SpecRequest, new PacketDelegate(recievedSpecRequestPacket));
            _net.RemovePacket((int)C2SPacketType.SetShip, new PacketDelegate(recievedSetShipPacket));
            _net.RemovePacket((int)C2SPacketType.SetFreq, new PacketDelegate(recievedSetFreqPacket));
            _net.RemovePacket((int)C2SPacketType.Die, new PacketDelegate(recievedDiePacket));
            _net.RemovePacket((int)C2SPacketType.Green, new PacketDelegate(recievedGreenPacket));
            _net.RemovePacket((int)C2SPacketType.AttachTo, new PacketDelegate(recievedAttachToPacket));
            _net.RemovePacket((int)C2SPacketType.TurretKickOff, new PacketDelegate(recievedTurretKickoffPacket));

            ArenaActionCallback.Unregister(_mm, arenaAction);
            PlayerActionCallback.Unregister(_mm, playerAction);
            NewPlayerCallback.Unregister(_mm, newPlayer);

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

            setFreq(p, freq);
        }

        void IGame.SetShip(Player p, ShipType ship)
        {
            if (p == null)
                return;

            setFreqAndShip(p, ship, p.Freq);
        }

        void IGame.SetFreqAndShip(Player p, ShipType ship, short freq)
        {
            setFreqAndShip(p, ship, freq);
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
            lockWork(target, true, notify, spec, timeout);
        }

        void IGame.Unlock(ITarget target, bool notify)
        {
            lockWork(target, false, notify, false, 0);
        }

        void IGame.LockArena(Arena arena, bool notify, bool onlyArenaState, bool initial, bool spec)
        {
            
        }

        void IGame.UnlockArena(Arena arena, bool notify, bool onlyArenaState)
        {
            
        }

        float IGame.GetIgnoreWeapons(Player p)
        {
            if (p == null)
                return 0;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return 0;

            return pd.ignoreWeapons / (float)Constants.RandMax;
        }

        void IGame.SetIgnoreWeapons(Player p, float proportion)
        {
            if (p == null)
                return;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return;

            pd.ignoreWeapons = (int)((float)Constants.RandMax * proportion);
        }

        void IGame.ShipReset(ITarget target)
        {
            if (target == null)
                throw new ArgumentNullException("target");

            _net.SendToTarget(target, _shipResetBytes, _shipResetBytes.Length, NetSendFlags.Reliable);
        }

        #endregion


        private void arenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null)
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                ArenaData ad = arena[_adkey] as ArenaData;

                // How often to check for region enter/exit events (in ticks).
                ad.regionCheckTime = _configManager.GetInt(arena.Cfg, "Misc", "RegionCheckInterval", 100) / 10; // asss has in centiseconds but we use milliseconds

                // Whether spectators can see extra data for the person they're spectating.
                ad.spec_epd = _configManager.GetInt(arena.Cfg, "Misc", "SpecSeeExtra", 1) != 0;

                // Whose energy levels spectators can see. The options are the
                // same as for Misc:SeeEnergy, with one addition: SEE_SPEC
                // means only the player you're spectating.
                ad.specNrg = (SeeEnergy)_configManager.GetInt(arena.Cfg, "Misc", "SpecSeeExtra", (int)SeeEnergy.All);

                // Whose energy levels everyone can see: SEE_NONE means nobody
                // else's, SEE_ALL is everyone's, SEE_TEAM is only teammates.
                ad.allNrg = (SeeEnergy)_configManager.GetInt(arena.Cfg, "Misc", "SeeEnergy", (int)SeeEnergy.None);

                ad.deathWithoutFiring = _configManager.GetInt(arena.Cfg, "Security", "MaxDeathWithoutFiring", 5);

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

                if (action == ArenaAction.Create)
                    ad.initLockship = ad.initSpec = false;
            }
        }

        private void playerAction(Player p, PlayerAction action, Arena arena)
        {
            if (p == null)
                return;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
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
                pd.lastRgnCheck = pd.changes.lastCheck = DateTime.Now;

                pd.lockship = ad.initLockship;
                if (ad.initSpec)
                {
                    p.Ship = ShipType.Spec;
                    p.Freq = (short)arena.SpecFreq;
                }

                p.Attached = -1;
            }
            else if (action == PlayerAction.EnterArena)
            {
                SeeEnergy seeNrg = SeeEnergy.None;
                SeeEnergy seeNrgSpec = SeeEnergy.None;
                SeeEnergy seePd = SeeEnergy.None;

                if (ad.allNrg != SeeEnergy.None)
                    seeNrg = ad.allNrg;

                if (ad.specNrg != SeeEnergy.None)
                    seeNrgSpec = ad.specNrg;

                if (ad.spec_epd)
                    seePd = SeeEnergy.All;

                if(_capabilityManager != null)
                {
                    if (_capabilityManager.HasCapability(p, Constants.Capabilities.SeeEnergy))
                        seeNrg = seeNrgSpec = SeeEnergy.All;

                    if (_capabilityManager.HasCapability(p, Constants.Capabilities.SeeExtraPlayerData))
                        seePd = SeeEnergy.All;
                }

                pd.pl_epd.seeNrg = seeNrg;
                pd.pl_epd.seeNrgSpec = seeNrgSpec;
                pd.pl_epd.seePd = seePd;
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
                            PlayerData idata = i[_pdkey] as PlayerData;
                            if (idata == null)
                                continue;

                            if (idata.speccing == p)
                                clearSpeccing(idata);
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }

                    if (pd.epdQueries > 0)
                        _logManager.LogP(LogLevel.Error, "Game", p, "extra position data queries is still nonzero");

                    clearSpeccing(pd);
                }

                pd.lastrgnset.Clear();
            }
        }

        private void newPlayer(Player p, bool isNew)
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
                            PlayerData idata = i[_pdkey] as PlayerData;
                            if (idata == null)
                                continue;

                            if (idata.speccing == p)
                                clearSpeccing(idata);
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }
                }
            }
        }

        private void clearSpeccing(PlayerData data)
        {
            if (data == null)
                return;

            lock (_specmtx)
            {
                if (data.speccing == null)
                    return;

                try
                {
                    if (data.pl_epd.seePd != SeeEnergy.None)
                    {
                        PlayerData odata = data.speccing[_pdkey] as PlayerData;
                        if (odata == null)
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

        private void addSpeccing(PlayerData data, Player t)
        {
            lock (_specmtx)
            {
                data.speccing = t;

                if (data.pl_epd.seePd != SeeEnergy.None)
                {
                    PlayerData tdata = t[_pdkey] as PlayerData;
                    if (tdata == null)
                        return;

                    if (tdata.epdQueries++ == 0)
                    {
                        // first time player is being specced by someone watching extra position data, tell the player to send extra position data
                        _net.SendToOne(t, _addSpecBytes, _addSpecBytes.Length, NetSendFlags.Reliable);
                    }
                }
            }
        }

        private void recievedPositionPacket(Player p, byte[] data, int len)
        {
            if (p == null || p.Status != PlayerState.Playing)
                return;

#if CFG_RELAX_LENGTH_CHECKS
            if(len < C2SPositionPacket.Length)
#else
            if(len != C2SPositionPacket.Length && len != C2SPositionPacket.LengthWithExtra)
#endif
            {
                _logManager.LogP(LogLevel.Malicious, "Game", p, "bad position packet len={0}", len);
                return;
            }

            Arena arena = p.Arena;
            if (arena == null || arena.Status != ArenaState.Running)
                return;

            bool isFake = p.Type == ClientType.Fake;
            if (!isFake)
            {
                byte checksum = 0;
                int left = 22;
                while ((left--) > 0)
                    checksum ^= data[left];

                if (checksum != 0)
                {
                    _logManager.LogP(LogLevel.Malicious, "Game", p, "bad position packet checksum");
                    return;
                }
            }

            C2SPositionPacket pos = new C2SPositionPacket(data);
            if (pos.X == -1 && pos.Y == -1)
            {
                // position sent after death, before respawn. these aren't
                // really useful for anything except making sure the server
                // knows the client hasn't dropped, so just ignore them.
                return;
            }

            Weapons weapon = pos.Weapon;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return;

            ArenaData ad = arena[_adkey] as ArenaData;
            if(ad == null)
                return;

            DateTime now = DateTime.Now;
            ServerTick gtc = ServerTick.Now;

            bool isNewer = pos.Time > pd.pos.Time;

            int latency = gtc - pos.Time;
            if (latency < 0)
                latency = 0;
            else if (latency > 255)
                latency = 255;

            int randnum = rand();

            // speccers don't get their position sent to anyone
            if (p.Ship != ShipType.Spec)
            {
                int x1 = pos.X;
                int y1 = pos.Y;
                //WeaponCodes weaponType = weapon.Type;

                // update region-based stuff once in a while, for real players only 
                if (isNewer && !isFake &&
                    (now - pd.lastRgnCheck).TotalMilliseconds >= ad.regionCheckTime)
                {
                    updateRegions(p, x1 >> 4, y1 >> 4);
                    pd.lastRgnCheck = now;
                }

                // this check should be before the weapon ignore hook
                if (pos.Weapon.Type != 0)
                {
                    p.Flags.SentWeaponPacket = true;
                    pd.deathWithoutFiring = 0;
                }

                // this is the weapons ignore hook. also ignore weapons based on region
                if ((pd.ignoreWeapons > 0 && rand() > pd.ignoreWeapons) ||
                    pd.rgnnoweapons)
                {
                    weapon.Type = 0;
                }

                // also turn off anti based on region
                if (pd.rgnnoanti)
                {
                    pos.Status &= ~PlayerPositionStatus.Antiwarp;
                }

                // if this is a plain position packet with no weapons, and is in
                // the wrong order, there's no need to send it. but fake players
                // never got data->pos.time initialized correctly, so do a
                // little special case.
                if (!isNewer && !isFake && weapon.Type == 0)
                    return;

                // by default, send unreliable droppable packets. weapons get a higher priority
                NetSendFlags nflags = NetSendFlags.Unreliable | NetSendFlags.Dropabble | 
                    (weapon.Type != 0 ? NetSendFlags.PriorityP5 : NetSendFlags.PriorityP3);

                // there are several reasons to send a weapon packet (05) instead of just a position one (28)
                bool sendWeapon = (
                    (weapon.Type != WeaponCodes.Null) || // a real weapon
                    (pos.Bounty > byte.MaxValue) || // bounty over 255
                    (p.Id > byte.MaxValue)); //pid over 255

                // send mines to everyone
                bool sendToAll = true;
                if ((weapon.Type == WeaponCodes.Bomb ||
                    weapon.Type == WeaponCodes.ProxBomb) && weapon.Alternate)
                {
                    sendToAll = true;
                }

                // send some percent of antiwarp positions to everyone
                if ((weapon.Type == WeaponCodes.Null) &&
                    ((pos.Status & PlayerPositionStatus.Antiwarp) != 0) &&
                    (rand() > cfg_sendanti))
                {
                    sendToAll = true;
                }

                // send safe zone enters to everyone, reliably
                if (((pos.Status & PlayerPositionStatus.Safezone) != 0) &&
                    ((p.Position.status & PlayerPositionStatus.Safezone) != 0))
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

                if (sendWeapon)
                {
                    int range = wpnRange[(int)weapon.Type];
                    using (DataBuffer buffer = Pool<DataBuffer>.Default.Get())
                    {
                        S2CWeaponsPacket wpn = new S2CWeaponsPacket(buffer.Bytes);
                        wpn.Type = (byte)S2CPacketType.Weapon;
                        wpn.Rotation = pos.Rotation;
                        wpn.Time = (ushort)(gtc & 0xFFFF);
                        wpn.X = pos.X;
                        wpn.YSpeed = pos.YSpeed;
                        wpn.PlayerId = (ushort)p.Id;
                        wpn.XSpeed = pos.XSpeed;
                        wpn.Checksum = 0;
                        wpn.Status = pos.Status;
                        wpn.C2SLatency = (byte)latency;
                        wpn.Y = pos.Y;
                        wpn.Bounty = pos.Bounty;
                        wpn.Weapon = pos.Weapon;
                        wpn.Extra = pos.Extra;

                        // move this field from the main packet to the extra data, in case they don't match.
                        ExtraPositionData epd = wpn.Extra;
                        epd.Energy = pos.Extra.Energy;

                        wpn.DoChecksum();

                        _playerData.Lock();
                        try
                        {
                            foreach (Player i in _playerData.PlayerList)
                            {
                                PlayerData idata = i[_pdkey] as PlayerData;
                                if (idata == null)
                                    continue;

                                if (i.Status == PlayerState.Playing &&
                                    i.IsStandard &&
                                    i.Arena == arena &&
                                    (i != p || p.Flags.SeeOwnPosition))
                                {
                                    int dist = hypot(x1 - idata.pos.X, y1 - idata.pos.Y);

                                    if (dist <= range ||
                                        sendToAll ||
                                        // send it always to specers
                                        idata.speccing == p ||
                                        // send it always to turreters
                                        i.Attached == p.Id ||
                                        // and send some radar packets
                                        (wpn.Weapon.Type == WeaponCodes.Null &&
                                            dist <= cfg_pospix &&
                                            randnum > ((float)dist / (float)cfg_pospix * (Constants.RandMax + 1.0f))) ||
                                        // bots
                                        i.Flags.SeeAllPositionPackets)
                                    {
                                        int plainLen = S2CWeaponsPacket.Length;
                                        int nrgLen = plainLen + 2;
                                        int epdLen = S2CWeaponsPacket.LengthWithExtra;


                                        if (wpn.Weapon.Type != WeaponCodes.Null)
                                            idata.wpnSent++;

                                        if (i.Ship == ShipType.Spec)
                                        {
                                            if ((idata.pl_epd.seePd != SeeEnergy.None) && (idata.speccing == p))
                                            {
                                                if (len >= 32)
                                                    _net.SendToOne(i, buffer.Bytes, epdLen, nflags);
                                                else
                                                    _net.SendToOne(i, buffer.Bytes, nrgLen, nflags);
                                            }
                                            else if (idata.pl_epd.seeNrgSpec == SeeEnergy.All ||
                                                (idata.pl_epd.seeNrgSpec == SeeEnergy.Team &&
                                                p.Freq == i.Freq) ||
                                                (idata.pl_epd.seeNrgSpec == SeeEnergy.Spec &&
                                                pd.speccing == p))
                                            {
                                                _net.SendToOne(i, buffer.Bytes, nrgLen, nflags);
                                            }
                                            else
                                            {
                                                _net.SendToOne(i, buffer.Bytes, plainLen, nflags);
                                            }
                                        }
                                        else if (idata.pl_epd.seeNrg == SeeEnergy.All ||
                                            (idata.pl_epd.seeNrg == SeeEnergy.Team &&
                                            p.Freq == i.Freq))
                                        {
                                            _net.SendToOne(i, buffer.Bytes, nrgLen, nflags);
                                        }
                                        else
                                        {
                                            _net.SendToOne(i, buffer.Bytes, plainLen, nflags);
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
                }
                else
                {
                    using (DataBuffer buffer = Pool<DataBuffer>.Default.Get())
                    {
                        S2CPositionPacket sendpos = new S2CPositionPacket(buffer.Bytes);
                        sendpos.Type = (byte)S2CPacketType.Position;
                        sendpos.Rotation = pos.Rotation;
                        sendpos.Time = (ushort)(gtc & 0xFFFF);
                        sendpos.X = pos.X;
                        sendpos.C2SLatency = (byte)latency;
                        sendpos.Bounty = (byte)pos.Bounty;
                        sendpos.PlayerId = (byte)p.Id;
                        sendpos.Status = pos.Status;
                        sendpos.YSpeed = pos.YSpeed;
                        sendpos.Y = pos.Y;
                        sendpos.XSpeed = pos.XSpeed;
                        sendpos.Extra = pos.Extra;

                        // move this field from the main packet to the extra data, in case they don't match.
                        ExtraPositionData extra = sendpos.Extra;
                        extra.Energy = (ushort)pos.Energy;

                        _playerData.Lock();
                        try
                        {
                            foreach (Player i in _playerData.PlayerList)
                            {
                                PlayerData idata = i[_pdkey] as PlayerData;
                                if (idata == null)
                                    continue;

                                if (i.Status == PlayerState.Playing &&
                                    i.IsStandard &&
                                    i.Arena == arena &&
                                    (i != p || p.Flags.SeeAllPositionPackets))
                                {
                                    int dist = hypot(x1 - idata.pos.X, y1 - idata.pos.Y);
                                    int res = i.Xres + i.Yres;

                                    if( dist<res ||
                                        sendToAll ||
                                        // send it always to speccers
                                        idata.speccing == p ||
                                        // send it always to turreters
                                        i.Attached == p.Id ||
                                        // and send some radar packets
                                        (dist <= cfg_pospix &&
                                        (randnum > ((float)dist / (float)cfg_pospix * (Constants.RandMax+1.0)))) ||
                                        // bots
                                        i.Flags.SeeAllPositionPackets)
                                    {
                                        int plainLen = S2CPositionPacket.Length;
                                        int nrgLen = plainLen + 2;
                                        int epdLen = S2CPositionPacket.LengthWithExtra;

                                        if (i.Ship == ShipType.Spec)
                                        {
                                            if (!(idata.pl_epd.seePd != SeeEnergy.None) && idata.speccing == p)
                                            {
                                                if(len >= 32)
                                                    _net.SendToOne(i, buffer.Bytes, epdLen, nflags);
                                                else
                                                    _net.SendToOne(i, buffer.Bytes, nrgLen, nflags);
                                            }
                                            else if (idata.pl_epd.seeNrgSpec == SeeEnergy.All ||
                                                (idata.pl_epd.seeNrgSpec == SeeEnergy.Team && p.Freq == i.Freq) ||
                                                (idata.pl_epd.seeNrgSpec == SeeEnergy.Spec && pd.speccing == p))
                                            {
                                                _net.SendToOne(i, buffer.Bytes, nrgLen, nflags);
                                            }
                                            else
                                            {
                                                _net.SendToOne(i, buffer.Bytes, plainLen, nflags);
                                            }
                                        }
                                        else if (idata.pl_epd.seeNrg == SeeEnergy.All ||
                                            (idata.pl_epd.seeNrg == SeeEnergy.Team && p.Freq == i.Freq))
                                        {
                                            _net.SendToOne(i, buffer.Bytes, nrgLen, nflags);
                                        }
                                        else
                                        {
                                            _net.SendToOne(i, buffer.Bytes, plainLen, nflags);
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
                }
            }

            // lag data
            //if(_lagCollect != null && !isFake)
                //_lagCollect.

            // only copy if the new one is later
            if (isNewer || isFake)
            {
                // TODO: make this asynchronous?
                if(((pos.Status ^ pd.pos.Status) & PlayerPositionStatus.Safezone) != 0 && !isFake)
                    fireSafeZoneEvent(arena, p, pos.X, pos.Y, pos.Status);

                // copy the whole thing. this will copy the epd, or, if the client
                // didn't send any epd, it will copy zeros because the buffer was
                // zeroed before data was recvd into it.
                pos.CopyTo(pd.pos);

                // update position in global player object.
                // only copy x/y if they are nonzero, so we keep track of last
                // non-zero position.
                if (pos.X != 0 || pos.Y != 0)
                {
                    p.Position.x = pos.X;
                    p.Position.y = pos.Y;
                }
                p.Position.xspeed = pos.XSpeed;
                p.Position.yspeed = pos.YSpeed;
                p.Position.rotation = pos.Rotation;
                p.Position.bounty = pos.Bounty;
                p.Position.status = pos.Status;
            }

            if (p.Flags.SentPositionPacket == false && !isFake)
            {
                p.Flags.SentPositionPacket = true;
                //_mainLoop.SetTimer<Player>(new TimerDelegate<Player>(runEnterGameCB), 0, 0, p, null);
                _mainLoop.RunInThread<Player>(new WorkerDelegate<Player>(runEnterGameCB), p);
            }
        }

        private void fireSafeZoneEvent(Arena arena, Player p, int x, int y, PlayerPositionStatus status)
        {
            if (p == null)
                return;

            if (arena != null)
                SafeZoneCallback.Fire(arena, p, x, y, status);
            else
                SafeZoneCallback.Fire(_mm, p, x, y, status);
        }

        private void runEnterGameCB(Player p)
        {
            if (p.Status == PlayerState.Playing)
                firePlayerActionEvent(p, PlayerAction.EnterGame, p.Arena);
        }

        private void firePlayerActionEvent(Player p, PlayerAction action, Arena arena)
        {
            if(p == null)
                return;

            if (arena != null)
                PlayerActionCallback.Fire(arena, p, action, arena);
            else
                PlayerActionCallback.Fire(_mm, p, action, arena);
        }

        private void updateRegions(Player p, int x, int y)
        {
            
        }

        private void recievedSpecRequestPacket(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len != 3)
            {
                _logManager.LogP(LogLevel.Malicious, "Game", p, "bad spec req packet len={0}", len);
                return;
            }

            if (p.Status != PlayerState.Playing || p.Ship != ShipType.Spec)
                return;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return;

            SimplePacket pkt = new SimplePacket(data);
            int tpid = pkt.D1;

            lock (_specmtx)
            {
                clearSpeccing(pd);

                if (tpid >= 0)
                {
                    Player t = _playerData.PidToPlayer(tpid);
                    if (t != null && t.Status == PlayerState.Playing && t.Ship != ShipType.Spec && t.Arena == p.Arena)
                        addSpeccing(pd, t);
                }
            }
        }

        private void recievedSetShipPacket(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len != 2)
            {
                _logManager.LogP(LogLevel.Malicious, "Game", p, "bad ship req packet len={0}", len);
                return;
            }

            Arena arena = p.Arena;
            if (p.Status != PlayerState.Playing || arena == null)
            {
                _logManager.LogP(LogLevel.Warn, "Game", p, "state sync problem: ship request from bad status");
                return;
            }

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return;

            ShipType ship = (ShipType)data[1];
            if (ship < ShipType.Warbird || ship > ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Malicious, "Game", p, "bad ship number: {0}", ship);
                return;
            }

            lock (_freqshipmtx)
            {
                if (p.Flags.DuringChange)
                {
                    _logManager.LogP(LogLevel.Warn, "Game", p, "state sync problem: ship request before ack from previous change");
                    return;
                }

                if (ship == p.Ship)
                {
                    _logManager.LogP(LogLevel.Warn, "Game", p, "state sync problem: already in requested ship");
                    return;
                }

                pd.changes.doExponentialDecay();

                if (pd.changes.changes > cfg_changelimit && cfg_changelimit > 0)
                {
                    _logManager.LogP(LogLevel.Info, "Game", p, "too many ship changes");

                    // disable for at least 30 seconds
                    pd.changes.changes |= (cfg_changelimit << 3);

                    if (_chat != null)
                        _chat.SendMessage(p, "You're changing ships too often, disabling for 30 seconds.");

                    return;
                }

                pd.changes.changes++;

                // do this bit while holding the mutex. it's ok to check the flag afterwards, though.
                expireLock(p);
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

            short freq = p.Freq;
            IFreqManager fm = _mm.GetInterface<IFreqManager>();
            if (fm != null)
            {
                try
                {
                    fm.ShipChange(p, ref ship, ref freq);
                }
                finally
                {
                    _mm.ReleaseInterface<IFreqManager>();
                }
            }

            setFreqAndShip(p, ship, freq);
        }

        private void recievedSetFreqPacket(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (len != 3)
                _logManager.LogP(LogLevel.Malicious, "Game", p, "bad freq req packet len={0}", len);
            else if (p.Flags.DuringChange)
                _logManager.LogP(LogLevel.Warn, "Game", p, "state sync problem: freq change before ack from previous change");
            else
            {
                SimplePacket pkt = new SimplePacket(data);
                freqChangeRequest(p, pkt.D1);
            }
        }

        private void freqChangeRequest(Player p, short freq)
        {
            if(p == null)
                return;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return;

            Arena arena = p.Arena;

            if (p.Status != PlayerState.Playing || arena == null)
            {
                _logManager.LogP(LogLevel.Malicious, "Game", p, "freq change from bad arena");
                return;
            }

            // check lock state
            lock (_freqshipmtx)
            {
                expireLock(p);
            }

            if (pd.lockship &&
                !(_capabilityManager != null && _capabilityManager.HasCapability(p, Constants.Capabilities.BypassLock)))
            {
                if (_chat != null)
                    _chat.SendMessage(p, "You have been locked in {0}.", (p.Ship == ShipType.Spec) ? "spectator mode" : "your ship");
                return;
            }

            ShipType ship = p.Ship;
            IFreqManager fm = _mm.GetInterface<IFreqManager>();
            if(fm != null)
            {
                try
                {
                    fm.FreqChange(p, ref ship, ref freq);
                }
                finally
                {
                    _mm.ReleaseInterface<IFreqManager>();
                }
            }

            if(ship == p.Ship)
                setFreq(p, freq);
            else
                setFreqAndShip(p, ship, freq);
        }

        private void setFreqAndShip(Player p, ShipType ship, short freq)
        {
            if (p == null)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return;

            if (p.Type == ClientType.Chat && ship != ShipType.Spec)
            {
                _logManager.LogP(LogLevel.Warn, "Game", p, "someone tried to forced chat client into playing ship");
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
                    clearSpeccing(pd);
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
                    _net.SendWithCallback(p, buffer.Bytes, ShipChangePacket.Length, resetDuringChange, null);
                }

                // send it to everyone else
                _net.SendToArena(arena, p, buffer.Bytes, ShipChangePacket.Length, NetSendFlags.Reliable);

                //if(_chatnet != null)

                fireShipChangeEvent(arena, p, ship, freq);
            }

            _logManager.LogP(LogLevel.Drivel, "Game", p, "changed ship/freq to ship {0}, freq {1}", ship, freq);
        }

        private void fireShipChangeEvent(Arena arena, Player p, ShipType ship, short freq)
        {
            if(p == null)
                return;

            if (arena != null)
                ShipChangeCallback.Fire(arena, p, ship, freq);
            else
                ShipChangeCallback.Fire(_mm, p, ship, freq);
        }

        private void setFreq(Player p, short freq)
        {
            if (p == null)
                return;

            if (freq < 0 || freq > 9999)
                return;

            Arena arena = p.Arena;
            if(arena == null)
                return;

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
                    _net.SendWithCallback(p, buffer.Bytes, 6, resetDuringChange, null);
                
                // everyone else
                _net.SendToArena(arena, p, buffer.Bytes, 6, NetSendFlags.Reliable);

                //if(_chatNet != null)

                fireFreqChangeEvent(arena, p, freq);
            }

            _logManager.LogP(LogLevel.Drivel, "Game", p, "changed freq to {0}", freq);
        }

        private void resetDuringChange(Player p, bool success, object dummy)
        {
            if (p == null)
                return;

            lock (_freqshipmtx)
            {
                p.Flags.DuringChange = false;
            }
        }

        private void fireFreqChangeEvent(Arena arena, Player p, short freq)
        {
            if (arena == null)
                return;

            if (p == null)
                return;

            FreqChangeCallback.Fire(arena, p, freq);
        }

        private void expireLock(Player p)
        {
            if (p == null)
                return;

            PlayerData pd = p[_pdkey] as PlayerData;
            if (pd == null)
                return;

            lock (_freqshipmtx)
            {
                if(pd.expires != null)
                    if (DateTime.Now > pd.expires)
                    {
                        pd.lockship = false;
                        pd.expires = null;
                        _logManager.LogP(LogLevel.Drivel, "Game", p, "lock expired");
                    }
            }
        }

        private void recievedDiePacket(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len != 5)
            {
                _logManager.LogP(LogLevel.Malicious, "Game", p, "bad death packet len={0}", len);
                return;
            }

            if (p.Status != PlayerState.Playing)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            SimplePacket dead = new SimplePacket(data);
            short bty = dead.D2;

            Player killer = _playerData.PidToPlayer(dead.D1);
            if (killer == null || killer.Status != PlayerState.Playing || killer.Arena != arena)
            {
                _logManager.LogP(LogLevel.Malicious, "Game", p, "reported kill by bad pid {0}", dead.D1);
                return;
            }

            short flagCount = p.pkt.FlagsCarried;

            // this will figure out how many points to send in the packet
            // NOTE: asss uses the event to set the points and green, i think it's best to keep it split between an interface call and event
            short pts = 0;
            Prize? green = null;

            IKillPoints kp = arena.GetInterface<IKillPoints>();
            if (kp != null)
            {
                try
                {
                    Prize g;
                    kp.GetKillPoints(arena, killer, p, bty, flagCount, out pts, out g);
                    green = g;
                }
                finally
                {
                    arena.ReleaseInterface<IKillPoints>();
                }
            }

            // record the kill points on our side
            if (pts != 0)
            {
                IStats stats = _mm.GetInterface<IStats>();
                if (stats != null)
                {
                    //stats.IncrementStats(killer, 
                    _mm.ReleaseInterface<IStats>();
                }
            }

            // pick a random green, if no one else has set one
            if (green == null)
            {
                IClientSettings cset = arena.GetInterface<IClientSettings>();
                if (cset != null)
                {
                    green = cset.GetRandomPrize(arena);
                    arena.ReleaseInterface<IClientSettings>();
                }
                else
                {
                    green = 0;
                }
            }

            fireKillEvent(arena, killer, p, bty, flagCount, pts, green.Value);

            notifyKill(killer, p, pts, flagCount, green.Value);

            firePostKillEvent(arena, killer, p, bty, flagCount, pts, green.Value);

            _logManager.LogA(LogLevel.Drivel, "Game", arena, "{0} killed by {1} (bty={2},flags={3},pts={4}", p.Name, killer.Name, bty, flagCount, pts);

            if (p.Flags.SentWeaponPacket == false)
            {
                PlayerData pd = p[_pdkey] as PlayerData;
                if (pd != null)
                {
                    ArenaData ad = arena[_adkey] as ArenaData;
                    if (ad != null)
                    {
                        if (pd.deathWithoutFiring++ == ad.deathWithoutFiring)
                        {
                            _logManager.LogP(LogLevel.Drivel, "Game", p, "specced for too many deaths without firing");
                            setFreqAndShip(p, ShipType.Spec, arena.SpecFreq);
                        }
                    }
                }
            }

            // reset this so we can accurately check deaths without firing
            p.Flags.SentWeaponPacket = false;
        }

        private void fireKillEvent(Arena arena, Player killer, Player killed, short bty, short flagCount, short pts, Prize green)
        {
            if (arena == null || killer == null || killed == null)
                return;

            KillCallback.Fire(arena, arena, killer, killed, bty, flagCount, pts, green);
        }

        private void firePostKillEvent(Arena arena, Player killer, Player killed, short bty, short flagCount, short pts, Prize green)
        {
            if (arena == null || killer == null || killed == null)
                return;

            PostKillCallback.Fire(arena, arena, killer, killed, bty, flagCount, pts, green);
        }

        private void notifyKill(Player killer, Player killed, short pts, short flagCount, Prize green)
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

        private void recievedGreenPacket(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len != GreenPacket.C2SLength)
            {
                _logManager.LogP(LogLevel.Malicious, "Game", p, "bad green packet len={0}", len);
                return;
            }

            if (p.Status != PlayerState.Playing)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            ArenaData ad = arena[_adkey] as ArenaData;
            if (ad == null)
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

            fireGreenEvent(arena, p, g.X, g.Y, prize);
        }

        private void fireGreenEvent(Arena arena, Player p, int x, int y, Prize prize)
        {
            if(p == null)
                return;

            if(arena != null)
                GreenCallback.Fire(arena, p, x, y, prize);
            else
                GreenCallback.Fire(_mm, p, x, y, prize);
        }

        private void recievedAttachToPacket(Player p, byte[] data, int len)
        {
            if (p == null || data == null)
                return;

            if (len != 3)
            {
                _logManager.LogP(LogLevel.Malicious, "Game", p, "bad attach req packet len={0}", len);
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
                    _logManager.LogP(LogLevel.Malicious, "Game", p, "tried to attach to bad pid {0}", pid2);
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

        private void recievedTurretKickoffPacket(Player p, byte[] data, int len)
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

        private int hypot(int dx, int dy)
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

        private int rand()
        {
            lock (random)
            {
                return random.Next(Constants.RandMax);
            }
        }

        private void lockWork(ITarget target, bool p, bool notify, bool spec, int timeout)
        {

        }
    }
}

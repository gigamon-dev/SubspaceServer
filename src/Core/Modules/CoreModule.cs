using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;
using SS.Utilities;
using SS.Core.ComponentInterfaces;
using System.IO;
using System.Diagnostics;
using SS.Core.ComponentCallbacks;

namespace SS.Core.Modules
{
    public static class AuthCodeExtension
    {
        /// <summary>
        /// which authentication result codes result in the player moving forward in the login process
        /// </summary>
        /// <param name="authCode"></param>
        /// <returns></returns>
        public static bool AuthIsOK(this AuthCode authCode)
        {
            return authCode == AuthCode.OK || authCode == AuthCode.SpecOnly || authCode == AuthCode.NoScores;
        }
    }

    /// <summary>
    /// this module handles player login process
    /// and transitioning the player object through its various states
    /// 
    /// that is, the entire lifetime of a player from getting authenticated by server, entering/leaving arenas, all the way to leaving the zone
    /// </summary>
    [CoreModuleInfo]
    public class CoreModule : IModule, IAuth
    {
        private static readonly byte[] _keepAlive = new byte[1] { (byte)Packets.S2CPacketType.KeepAlive };

        private ModuleManager _mm;
        private IPlayerData _playerData;
        private INetwork _net;
        private IChatNet _chatnet;
        private ILogManager _logManager;
        private IConfigManager _configManager;
        private IServerTimer _mainLoop;
        private IMapNewsDownload _map;
        private IArenaManagerCore _arenaManager;
        private ICapabilityManager _capabiltyManager;
        private IPersist _persist;
        private IStats _stats;

        private int _pdkey;

        private const ushort ClientVersion_VIE = 134;
        private const ushort ClientVersion_Cont = 40;

        private const string ContinuumExeFile = "clients/continuum.exe";
        private const string ContinuumChecksumFile = "scrty";

        private uint _continuumChecksum;
        private uint _codeChecksum;

        private class CorePlayerData
        {
            public AuthData authdata;
            public LoginPacket? loginpkt;
            public int lplen;
            public Player replacedBy;

            // TODO: maybe use BitVector
            public bool hasdonegsync; // global sync
            public bool hasdoneasync; // arena sync
            public bool hasdonegcallbacks; // global callbacks
        }

        private uint getChecksum(string file)
        {
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                {
                    Ionic.Crc.CRC32 crc32 = new Ionic.Crc.CRC32();
                    return (uint)crc32.GetCrc32(fs);
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(CoreModule), "error getting checksum to [{0}]: {1}", file, ex.Message);
                return uint.MaxValue;
            }
        }

        private uint getU32(string file, int offset)
        {
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    return br.ReadUInt32();
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(CoreModule), "error getting u32 from '{0}' at offset {1}: {2}", file, offset, ex.Message);
                return uint.MaxValue;
            }
        }

        #region IModule Members

        Type[] IModule.InterfaceDependencies { get; } = new Type[] 
        {
            typeof(IPlayerData), 
            typeof(INetwork), 
            typeof(ILogManager), 
            typeof(IConfigManager), 
            typeof(IServerTimer), 
            typeof(IMapNewsDownload), 
            typeof(IArenaManagerCore), 
            typeof(ICapabilityManager), 
        };

        bool IModule.Load(ModuleManager mm, IReadOnlyDictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;
            _playerData = interfaceDependencies[typeof(IPlayerData)] as IPlayerData;
            _net = interfaceDependencies[typeof(INetwork)] as INetwork;
            //_chatnet = 
            _logManager = interfaceDependencies[typeof(ILogManager)] as ILogManager;
            _configManager = interfaceDependencies[typeof(IConfigManager)] as IConfigManager;
            _mainLoop = interfaceDependencies[typeof(IServerTimer)] as IServerTimer;
            _map = interfaceDependencies[typeof(IMapNewsDownload)] as IMapNewsDownload;
            _arenaManager = interfaceDependencies[typeof(IArenaManagerCore)] as IArenaManagerCore;
            _capabiltyManager = interfaceDependencies[typeof(ICapabilityManager)] as ICapabilityManager;

            _continuumChecksum = getChecksum(ContinuumExeFile);
            _codeChecksum = getU32(ContinuumChecksumFile, 4);

            _pdkey = _playerData.AllocatePlayerData<CorePlayerData>();

            // set up callbacks
            _net.AddPacket((int)Packets.C2SPacketType.Login, playerLogin);
            _net.AddPacket((int)Packets.C2SPacketType.ContLogin, playerLogin);

            //if(_chatnet != null)
                //_chatnet.AddHandler("LOGIN", chatLogin);

            _mainLoop.SetTimer(processPlayerStates, 100, 100, null);

            // register default interfaces which may be replaced later
            mm.RegisterInterface<IAuth>(this);

            // set up periodic events
            _mainLoop.SetTimer(sendKeepAlive, 5000, 5000, null); // every 5 seconds

            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            if (_mm.UnregisterInterface<IAuth>() != 0)
                return false;

            _mainLoop.ClearTimer(sendKeepAlive, null);
            _mainLoop.ClearTimer(processPlayerStates, null);
            
            if (_net != null)
            {
                _net.RemovePacket((int)Packets.C2SPacketType.Login, playerLogin);
                _net.RemovePacket((int)Packets.C2SPacketType.ContLogin, playerLogin);
            }

            //if (_chatnet != null)
                //_chatnet.RemoveHandler("LOGIN", chatLogin);

            _playerData.FreePlayerData(_pdkey);
            return true;
        }

        #endregion

        private struct PlayerStateChange
        {
            public Player Player;
            public PlayerState OldStatus;
        }

        private bool processPlayerStates()
        {
            // put pending actions here while processing the player list
            LinkedList<PlayerStateChange> actions = null; // only allocate if we need to

            _playerData.WriteLock();

            try
            {
                PlayerState ns;
                foreach (Player player in _playerData.PlayerList)
                {
                    PlayerState oldstatus = player.Status;
                    switch (oldstatus)
                    {
                        // for all of these states, there's nothing to do in this loop
                        case PlayerState.Uninitialized:
                        case PlayerState.WaitAuth:
                        case PlayerState.WaitGlobalSync1:
                        case PlayerState.WaitGlobalSync2:
                        case PlayerState.WaitArenaSync1:
                        case PlayerState.WaitArenaSync2:
                        case PlayerState.Playing:
                        case PlayerState.TimeWait:
                            continue;

                        // this is an interesting state: this function is
                        // responsible for some transitions away from loggedin. we
                        // also do the whenloggedin transition if the player is just
                        // connected and not logged in yet.
                        case PlayerState.Connected:
                        case PlayerState.LoggedIn:
                            // at this point, the player can't have an arena
                            player.Arena = null;

                            // check if the player's arena is ready.
                            // LOCK: we don't grab the arena status lock because it
                            // doesn't matter if we miss it this time around
                            if (player.NewArena != null && player.NewArena.Status == ArenaState.Running)
                            {
                                player.Arena = player.NewArena;
                                player.NewArena = null;
                                player.Status = PlayerState.DoFreqAndArenaSync;
                            }

                            // check whenloggedin. this is used to move players to
                            // the leaving_zone status once various things are completed
                            if (player.WhenLoggedIn != PlayerState.Uninitialized)
                            {
                                player.Status = player.WhenLoggedIn;
                                player.WhenLoggedIn = PlayerState.Uninitialized;
                            }
                            continue;

                        // these states automatically transition to another one. set
                        // the new status first, then take the appropriate action below
                        case PlayerState.NeedAuth: ns = PlayerState.WaitAuth; break;
                        case PlayerState.NeedGlobalSync: ns = PlayerState.WaitGlobalSync1; break;
                        case PlayerState.DoGlobalCallbacks: ns = PlayerState.SendLoginResponse; break;
                        case PlayerState.SendLoginResponse: ns = PlayerState.LoggedIn; break;
                        case PlayerState.DoFreqAndArenaSync: ns = PlayerState.WaitArenaSync1; break;
                        case PlayerState.ArenaRespAndCBS: ns = PlayerState.Playing; break;
                        case PlayerState.LeavingArena: ns = PlayerState.DoArenaSync2; break;
                        case PlayerState.DoArenaSync2: ns = PlayerState.WaitArenaSync2; break;
                        case PlayerState.LeavingZone: ns = PlayerState.WaitGlobalSync2; break;

                        default: // catch any other state
                            _logManager.LogM(LogLevel.Error, nameof(CoreModule), "[pid={0}] internal error: unknown player status {1}", player.Id, oldstatus);
                            continue;
                    }

                    player.Status = ns;

                    if(actions == null)
                        actions = new LinkedList<PlayerStateChange>();

                    // add this player to the pending actions list, to be run when we release the status lock.
                    PlayerStateChange action = new PlayerStateChange();
                    action.Player = player;
                    action.OldStatus = oldstatus;
                    actions.AddLast(action);
                }
            }
            finally
            {
                _playerData.WriteUnlock();
            }

            if (actions == null)
                return true;

            Arena arena;
            foreach (PlayerStateChange action in actions)
            {
                Player player = action.Player;
                PlayerState oldstatus = action.OldStatus;
                CorePlayerData pdata = player[_pdkey] as CorePlayerData;
                if (pdata == null)
                    continue;

                switch (oldstatus)
                {
                    case PlayerState.NeedAuth:
                        {
                            IAuth auth = _mm.GetInterface<IAuth>();
                            try
                            {
                                if (auth != null && pdata.loginpkt != null && pdata.lplen > 0)
                                {
                                    _logManager.LogM(LogLevel.Drivel, nameof(CoreModule), "authenticating with '{0}'", auth.GetType().ToString());
                                    auth.Authenticate(player, pdata.loginpkt.Value, pdata.lplen, authDone);
                                }
                                else
                                {
                                    _logManager.LogM(LogLevel.Warn, nameof(CoreModule), "can't authenticate player!");
                                    _playerData.KickPlayer(player);
                                }

                                pdata.loginpkt = null;
                                pdata.lplen = 0;
                            }
                            finally
                            {
                                if(auth != null)
                                    _mm.ReleaseInterface<IAuth>();
                            }
                        }
                        break;

                    case PlayerState.NeedGlobalSync:
                        //if (_persist)
                            //_persist.GetPlayer(player, null, playerSyncDone);
                        //else
                            playerSyncDone(player);
                        pdata.hasdonegsync = true;
                        break;

                    case PlayerState.DoGlobalCallbacks:
                        firePlayerActionEvent(player, PlayerAction.Connect, null);
                        pdata.hasdonegcallbacks = true;
                        break;

                    case PlayerState.SendLoginResponse:
                        sendLoginResponse(player);
                        _logManager.LogM(LogLevel.Info, nameof(CoreModule), "[{0}] [pid={1}] player logged in", player.Name, player.Id);
                        break;

                    case PlayerState.DoFreqAndArenaSync:
                        // the arena will be fully loaded here
                        ShipType requestedShip = player.Ship;
                        player.Ship = (ShipType)(-1);
                        player.Freq = -1;

                        // do pre-callbacks
                        arena = player.Arena;
                        firePlayerActionEvent(player, PlayerAction.PreEnterArena, arena);

                        // get a freq
                        if ((int)player.Ship == -1 || player.Freq == -1)
                        {
                            ShipType ship = player.Ship = requestedShip;
                            short freq = player.Freq = 0;

                            IFreqManager fm = _mm.GetInterface<IFreqManager>();
                            if (fm != null)
                            {
                                try
                                {
                                    fm.InitialFreq(player, ref ship, ref freq);
                                }
                                finally
                                {
                                    _mm.ReleaseInterface<IFreqManager>();
                                }
                            }

                            // set results back
                            player.Ship = ship;
                            player.Freq = freq;
                        }

                        // sync scores
                        //if (persist)
                            //persist.GetPlayer(player, player.Arena, playerSyncDone);
                        //else
                            playerSyncDone(player);
                        pdata.hasdonegsync = true;
                        break;

                    case PlayerState.ArenaRespAndCBS:
                        /*if (stats != null)
                        {
                            // try to get scores in pdata packet
                            player.pkt.KillPoints = _stats.GetStat(player, Stat.KillPoints, StatInterval.Reset);
                            player.pkt.FlagPoints = _stats.GetStat(player, Stat.FlagPoints, StatInterval.Reset);
                            player.pkt.Wins = stats.GetStat(player, Stat.Kills, StatInterval.Reset);
                            player.pkt.Losses = stats.GetStat(player, Stat.Deaths, StatInterval.Reset);

                            // also get other player's scores into their pdatas
                            stats.SendUpdates(player);
                        }*/

                        _arenaManager.SendArenaResponse(player);
                        player.Flags.SentPositionPacket = false;
                        player.Flags.SentWeaponPacket = false;

                        arena = player.Arena;
                        firePlayerActionEvent(player, PlayerAction.EnterArena, arena);

                        break;

                    case PlayerState.LeavingArena:
                        firePlayerActionEvent(player, PlayerAction.LeaveArena, player.Arena);
                        break;

                    case PlayerState.DoArenaSync2:
                        /*if (persist != null && pdata.hasdonegsync)
                            persist.PutPlayer(player, player.Arena, playerSyncDone);
                        else*/
                            playerSyncDone(player);
                        pdata.hasdoneasync = false;
                        break;

                    case PlayerState.LeavingZone:
                        if (pdata.hasdonegcallbacks)
                            firePlayerActionEvent(player, PlayerAction.Disconnect, null);

                        /*if (_persist != null && pdata.hasdonegsync)
                            _persist.PutPlayer(player, null, playerSyncDone);
                        else*/
                            playerSyncDone(player);

                        pdata.hasdonegsync = false;
                        break;
                }
            }

            return true;
        }

        private void firePlayerActionEvent(Player p, PlayerAction action, Arena arena)
        {
            if (p == null)
                return;

            if (arena != null)
                PlayerActionCallback.Fire(arena, p, action, arena);
            else
                PlayerActionCallback.Fire(_mm, p, action, arena);
        }

        private void failLoginWith(Player p, AuthCode authCode, string text, string logmsg)
        {
            AuthData auth = new AuthData();

            if (p.Type == ClientType.Continuum && text != null)
            {
                auth.code = AuthCode.CustomText;
                auth.customtext = text;
            }
            else
                auth.code = authCode;

            _playerData.WriteLock();
            try
            {
                p.Status = PlayerState.WaitAuth;
            }
            finally
            {
                _playerData.WriteUnlock();
            }

            authDone(p, auth);

            _logManager.LogM(LogLevel.Drivel, nameof(CoreModule), "[pid={0}] login request denied: {1}", p.Id, logmsg);
        }

        private void playerLogin(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            CorePlayerData pdata = p[_pdkey] as CorePlayerData;
            if (pdata == null)
                return;

            if (!p.IsStandard)
            {
                _logManager.LogM(LogLevel.Malicious, nameof(CoreModule), "[pid={0}] login packet from wrong client type ({1})", p.Id, p.Type);
            }
#if CFG_RELAX_LENGTH_CHECKS
            else if ((p.Type == ClientType.VIE && len < LoginPacket.LengthVIE) ||
                (p.Type == ClientType.Continuum && len < LoginPacket.LengthContinuum))
#endif
            else if ((p.Type == ClientType.VIE && len != LoginPacket.LengthVIE) ||
                (p.Type == ClientType.Continuum && len != LoginPacket.LengthContinuum))
                _logManager.LogM(LogLevel.Malicious, nameof(CoreModule), "[pid={0}] bad login packet length ({1})", p.Id, len);
            else if (p.Status != PlayerState.Connected)
                _logManager.LogM(LogLevel.Malicious, nameof(CoreModule), "[pid={0}] login request from wrong stage: {1}", p.Id, p.Status);
            else
            {
                LoginPacket pkt = new LoginPacket(data);

#if !CFG_RELAX_LENGTH_CHECKS
                // VIE clients can only have one version. 
                // Continuum clients will need to ask for an update
                if(p.Type == ClientType.VIE && pkt.CVersion != ClientVersion_VIE)
                {
                    failLoginWith(p, AuthCode.LockedOut, null, "bad VIE client version");
                    return;
                }
#endif

                // copy into (per player) storage for use by authenticator
                if (len > 512)
                    len = 512;

                pdata.loginpkt = new LoginPacket(new byte[len]); // consider getting byte[] from a pool
                LoginPacket lp = pdata.loginpkt.Value;
                LoginPacket.Copy(pkt, lp);
                pdata.lplen = len;

                // name must be nul-terminated, also set name length limit at 19 characters
                string name = lp.Name.Trim();
                if (name.Length > 19)
                    name = name.Substring(0, 19); // TODO: asss cuts at 19 b/c of null termination, but i think we could potentially do 20

                // only allow printable characters in names, excluding colon.
                // while we're at it, remove leading, trailing, and series of spaces
                // TODO:
                name = name.Replace(":", string.Empty);

                // if nothing could be salvaged from their name, disconnect them
                if (name == string.Empty)
                {
                    failLoginWith(p, AuthCode.BadName, "Your player name contains no valid characters", "all invalid chars");
                    return;
                }

                // must start with number or letter
                if (!char.IsLetterOrDigit(name[0]))
                {
                    failLoginWith(p, AuthCode.BadName, "Your player name must start with a letter or number", "name doesn't start with alphanumeric");
                    return;
                }

                // pass must be nul-terminated

                // fill misc data
                p.MacId = pkt.MacId;
                p.PermId = pkt.D2;

                if (p.Type == ClientType.VIE)
                    p.ClientName = string.Format("<ss/vie client, v. {0}>", pkt.CVersion);
                else if(p.Type == ClientType.Continuum)
                    p.ClientName = string.Format("<continuum, v. {0}>", pkt.CVersion);

                // set up status
                _playerData.WriteLock();
                try
                {
                    p.Status = PlayerState.NeedAuth;
                }
                finally
                {
                    _playerData.WriteUnlock();
                }

                _logManager.LogM(LogLevel.Drivel, nameof(CoreModule), "[pid={0}] login request: '{1}'", p.Id, lp.Name);
            }
        }


        private void authDone(Player p, AuthData auth)
        {
            CorePlayerData pdata = p[_pdkey] as CorePlayerData;
            if (pdata == null)
                return;

            if (p.Status != PlayerState.WaitAuth)
            {
                _logManager.LogM(LogLevel.Warn, nameof(CoreModule), "[pid={0}] AuthDone called from wrong stage: {1}", p.Id, p.Status);
                return;
            }

            // copy the authdata
            pdata.authdata = auth;

            p.Flags.Authenticated = auth.authenticated;

            if (auth.code.AuthIsOK())
            {
                // login succeeded

                // try to locate existing player with the same name
                Player oldp = _playerData.FindPlayer(auth.name);

                // set new player's name
                p.pkt.Name = auth.sendname;
                p.Name = auth.name;
                p.pkt.Squad = auth.squad;
                p.Squad = auth.squad;

                // make sure we don't have two identical players. if so, do not
                // increment stage yet. we'll do it when the other player leaves
                if (oldp != null && oldp != p)
                {
                    CorePlayerData oldd = p[_pdkey] as CorePlayerData;
                    if (oldd == null)
                        return;

                    _logManager.LogM(LogLevel.Drivel, nameof(CoreModule), "[{0}] player already on, kicking him off (pid {1} replacing {2})", auth.name, p.Id, oldp.Id);
                    oldd.replacedBy = p;
                    _playerData.KickPlayer(oldp);
                }
                else
                {
                    // increment stage
                    _playerData.WriteLock();
                    p.Status = PlayerState.NeedGlobalSync;
                    _playerData.WriteUnlock();
                }
            }
            else
            {
                // if the login didn't succeed status should go to S_CONNECTED
                // instead of moving forward, and send the login response now,
                // since we won't do it later.
                sendLoginResponse(p);
                _playerData.WriteLock();
                try
                {
                    p.Status = PlayerState.Connected;
                }
                finally
                {
                    _playerData.WriteUnlock();
                }
            }
        }

        private void playerSyncDone(Player player)
        {
            if (player == null)
                return;

            _playerData.WriteLock();

            try
            {
                if (player.Status == PlayerState.WaitArenaSync1)
                {
                    if (!player.Flags.LeaveArenaWhenDoneWaiting)
                        player.Status = PlayerState.ArenaRespAndCBS; // note: this is the route it takes to get to the Playing state
                    else
                        player.Status = PlayerState.DoArenaSync2;
                }
                else if (player.Status == PlayerState.WaitArenaSync2)
                    player.Status = PlayerState.LoggedIn;
                else if (player.Status == PlayerState.WaitGlobalSync1)
                    player.Status = PlayerState.DoGlobalCallbacks;
                else if (player.Status == PlayerState.WaitGlobalSync2)
                {
                    CorePlayerData pdata = player[_pdkey] as CorePlayerData;
                    Player replacedBy = pdata.replacedBy;
                    if (replacedBy != null)
                    {
                        if (replacedBy.Status != PlayerState.WaitAuth)
                        {
                            _logManager.LogM(LogLevel.Warn, nameof(CoreModule), "[oldpid={0}] [newpid={1}] unexpected status when replacing players: {2}", player.Id, replacedBy.Id, replacedBy.Status);
                        }
                        else
                        {
                            replacedBy.Status = PlayerState.NeedGlobalSync;
                            pdata.replacedBy = null;
                        }
                    }

                    player.Status = PlayerState.TimeWait;
                }
                else
                {
                    _logManager.LogM(LogLevel.Warn, nameof(CoreModule), "[pid={0}] player_sync_done called from wrong status: {1}", player.Id, player.Status);
                }
            }
            finally
            {
                _playerData.WriteUnlock();
            }
        }

        private static string getAuthCodeMessage(AuthCode code)
        {
            switch (code)
            {
                case AuthCode.OK: return "ok";
                case AuthCode.NewName: return "new user";
                case AuthCode.BadPassword: return "incorrect password";
                case AuthCode.ArenaFull: return "arena full";
                case AuthCode.LockedOut: return "you have been locked out";
                case AuthCode.NoPermission: return "no permission";
                case AuthCode.SpecOnly: return "you can spec only";
                case AuthCode.TooManyPoints: return "you have too many points";
                case AuthCode.TooSlow: return "too slow (?)";
                case AuthCode.NoPermission2: return "no permission (2)";
                case AuthCode.NoNewConn: return "the server is not accepting new connections";
                case AuthCode.BadName: return "bad player name";
                case AuthCode.OffensiveName: return "offensive player name";
                case AuthCode.NoScores: return "the server is not recordng scores";
                case AuthCode.ServerBusy: return "the server is busy";
                case AuthCode.TooLowUsage: return "too low usage";
                case AuthCode.NoName: return "no name sent";
                case AuthCode.TooManyDemo: return "too many demo players";
                case AuthCode.NoDemo: return "no demo players allowed";

                case AuthCode.CustomText:
                default: 
                    return "???";
            }
        }

        private void sendLoginResponse(Player player)
        {
            CorePlayerData pdata = player[_pdkey] as CorePlayerData;
            if (pdata == null)
                return;

            AuthData auth = pdata.authdata;

            if (auth == null)
            {
                _logManager.LogM(LogLevel.Error, nameof(CoreModule), "missing authdata for pid {0}", player.Id);
                _playerData.KickPlayer(player);
            }
            else if (player.IsStandard)
            {
                using (DataBuffer buffer = Pool<DataBuffer>.Default.Get())
                {
                    LoginResponsePacket lr = new LoginResponsePacket(buffer.Bytes);
                    lr.Initialize();
                    lr.Code = (byte)auth.code;
                    lr.DemoData = auth.demodata ? (byte)1 : (byte)0;
                    lr.NewsChecksum = _map.GetNewsChecksum();

                    if (player.Type == ClientType.Continuum)
                    {
                        using (DataBuffer contVersionBuffer = Pool<DataBuffer>.Default.Get())
                        {
                            ContinuumChecksumPacket pkt = new ContinuumChecksumPacket(contVersionBuffer.Bytes);
                            pkt.Type = (byte)Packets.S2CPacketType.ContVersion;
                            pkt.ContVersion = ClientVersion_Cont;
                            pkt.Checksum = _continuumChecksum;

                            _net.SendToOne(player, contVersionBuffer.Bytes, ContinuumChecksumPacket.Length, NetSendFlags.Reliable);
                        }

                        lr.ExeChecksum = _continuumChecksum;
                        lr.CodeChecksum = _codeChecksum;
                    }
                    else
                    {
                        // old VIE exe checksums
                        lr.ExeChecksum = 0xF1429CE8;
                        lr.CodeChecksum = 0x281CC948;
                    }

                    if (_capabiltyManager != null && _capabiltyManager.HasCapability(player, Constants.Capabilities.SeePrivFreq))
                    {
                        // to make the client think it's a mod
                        lr.ExeChecksum = uint.MaxValue;
                        lr.CodeChecksum = uint.MaxValue;
                    }

                    if (lr.Code == (byte)AuthCode.CustomText)
                    {
                        if (player.Type == ClientType.Continuum)
                        {
                            // send custom rejection text
                            using (DataBuffer loginTextBuffer = Pool<DataBuffer>.Default.Get())
                            {
                                loginTextBuffer.Bytes[0] = (byte)Packets.S2CPacketType.LoginText;
                                int numCharacters = auth.customtext.Length <= 255 ? auth.customtext.Length : 255;
                                Encoding.ASCII.GetBytes(auth.customtext, 0, numCharacters, loginTextBuffer.Bytes, 1);
                                
                                // not sure why +2 instead of +1
                                _net.SendToOne(player, loginTextBuffer.Bytes, numCharacters + 2, NetSendFlags.Reliable);
                            }
                        }
                        else
                        {
                            // VIE doesn't understand that packet
                            lr.Code = (byte)AuthCode.LockedOut;
                        }
                    }

                    _net.SendToOne(player, buffer.Bytes, LoginResponsePacket.Length, NetSendFlags.Reliable);
                }
            }
            else if (player.IsChat)
            {
                // TODO
            }

            pdata.authdata = null;
        }

        #region IAuth Members

        void IAuth.Authenticate(Player p, LoginPacket lp, int lplen, AuthDoneDelegate done)
        {
            defaultAuth(p, lp, lplen, done);   
        }

        #endregion

        private void defaultAuth(Player p, LoginPacket lp, int lplen, AuthDoneDelegate done)
        {
            AuthData auth = new AuthData();

            auth.demodata = false;
            auth.code = AuthCode.OK;
            auth.authenticated = false;

            string name = lp.Name;
            auth.name = name.Length > 23 ? name.Substring(0, 23) : name;
            auth.sendname = name.Length > 19 ? name.Substring(0, 19) : name;
            auth.squad = null;

            done(p, auth);
        }

        private bool sendKeepAlive()
        {
            if (_net != null)
            {
                _net.SendToArena(null, null, _keepAlive, 1, NetSendFlags.Reliable);
            }
            return true;
        }
    }
}

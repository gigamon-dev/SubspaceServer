using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// this module handles player login process
    /// and transitioning the player object through its various states
    /// 
    /// that is, the entire lifetime of a player from getting authenticated by server, entering/leaving arenas, all the way to leaving the zone
    /// </summary>
    [CoreModuleInfo]
    public class Core : IModule, IAuth
    {
        private static readonly byte[] _keepAlive = new byte[1] { (byte)S2CPacketType.KeepAlive };

        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private ICapabilityManager _capabiltyManager;
        //private IChatNet _chatnet;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMainloopTimer _mainloopTimer;
        private IMapNewsDownload _map;
        private INetwork _net;
        //private IPersist _persist;
        private IPlayerData _playerData;
        //private IStats _stats;
        private InterfaceRegistrationToken _iAuthToken;

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
            public DataBuffer LoginPacketBuffer;
            public Player replacedBy;

            // TODO: maybe use BitVector
            public bool hasdonegsync; // global sync
            public bool hasdoneasync; // arena sync
            public bool hasdonegcallbacks; // global callbacks
        }

        private uint GetChecksum(string file)
        {
            try
            {
                using FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read);
                Ionic.Crc.CRC32 crc32 = new Ionic.Crc.CRC32();
                return (uint)crc32.GetCrc32(fs);
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(Core), "Error getting checksum to '{0}'. {1}", file, ex.Message);
                return uint.MaxValue;
            }
        }

        private uint GetUInt32(string file, int offset)
        {
            try
            {
                using FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read);
                using BinaryReader br = new BinaryReader(fs);
                fs.Seek(offset, SeekOrigin.Begin);
                return br.ReadUInt32();
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(Core), "Error getting UInt32 from '{0}' at offset {1}. {2}", file, offset, ex.Message);
                return uint.MaxValue;
            }
        }

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            ICapabilityManager capabilityManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloopTimer mainloopTimer,
            IMapNewsDownload map,
            INetwork net,
            IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _capabiltyManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _continuumChecksum = GetChecksum(ContinuumExeFile);
            _codeChecksum = GetUInt32(ContinuumChecksumFile, 4);

            _pdkey = _playerData.AllocatePlayerData<CorePlayerData>();

            // set up callbacks
            _net.AddPacket(C2SPacketType.Login, Packet_Login);
            _net.AddPacket(C2SPacketType.ContLogin, Packet_Login);

            //if(_chatnet != null)
                //_chatnet.AddHandler("LOGIN", chatLogin);

            _mainloopTimer.SetTimer(MainloopTimer_ProcessPlayerStates, 100, 100, null);

            // register default interface which may be replaced later
            _iAuthToken = broker.RegisterInterface<IAuth>(this);

            // set up periodic events
            _mainloopTimer.SetTimer(MainloopTimer_SendKeepAlive, 5000, 5000, null); // every 5 seconds

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<IAuth>(ref _iAuthToken) != 0)
                return false;

            _mainloopTimer.ClearTimer(MainloopTimer_SendKeepAlive, null);
            _mainloopTimer.ClearTimer(MainloopTimer_ProcessPlayerStates, null);
            
            _net.RemovePacket(C2SPacketType.Login, Packet_Login);
            _net.RemovePacket(C2SPacketType.ContLogin, Packet_Login);

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

        private bool MainloopTimer_ProcessPlayerStates()
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
                            _logManager.LogM(LogLevel.Error, nameof(Core), "[pid={0}] Internal error: unknown player status {1}", player.Id, oldstatus);
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

                if (!(player[_pdkey] is CorePlayerData pdata))
                    continue;

                switch (oldstatus)
                {
                    case PlayerState.NeedAuth:
                        {
                            IAuth auth = _broker.GetInterface<IAuth>();
                            try
                            {
                                if (auth != null && pdata.LoginPacketBuffer != null && pdata.LoginPacketBuffer.NumBytes > 0)
                                {
                                    _logManager.LogM(LogLevel.Drivel, nameof(Core), "Authenticating with '{0}'", auth.GetType().ToString());
                                    auth.Authenticate(player, new LoginPacket(pdata.LoginPacketBuffer.Bytes), pdata.LoginPacketBuffer.NumBytes, AuthDone);
                                }
                                else
                                {
                                    _logManager.LogM(LogLevel.Warn, nameof(Core), "Can't authenticate player!");
                                    _playerData.KickPlayer(player);
                                }

                                pdata.LoginPacketBuffer?.Dispose();
                            }
                            finally
                            {
                                if(auth != null)
                                    _broker.ReleaseInterface(ref auth);
                            }
                        }
                        break;

                    case PlayerState.NeedGlobalSync:
                        //if (_persist)
                            //_persist.GetPlayer(player, null, playerSyncDone);
                        //else
                            PlayerSyncDone(player);
                        pdata.hasdonegsync = true;
                        break;

                    case PlayerState.DoGlobalCallbacks:
                        FirePlayerActionEvent(player, PlayerAction.Connect, null);
                        pdata.hasdonegcallbacks = true;
                        break;

                    case PlayerState.SendLoginResponse:
                        SendLoginResponse(player);
                        _logManager.LogM(LogLevel.Info, nameof(Core), "[{0}] [pid={1}] Player logged in from ip={2} macid={3:X}", player.Name, player.Id, player.IpAddress, player.MacId);
                        break;

                    case PlayerState.DoFreqAndArenaSync:
                        // the arena will be fully loaded here
                        ShipType requestedShip = player.Ship;
                        player.Ship = (ShipType)(-1);
                        player.Freq = -1;

                        // do pre-callbacks
                        arena = player.Arena;
                        FirePlayerActionEvent(player, PlayerAction.PreEnterArena, arena);

                        // get a freq
                        if ((int)player.Ship == -1 || player.Freq == -1)
                        {
                            ShipType ship = player.Ship = requestedShip;
                            short freq = player.Freq = 0;

                            IFreqManager fm = _broker.GetInterface<IFreqManager>();
                            if (fm != null)
                            {
                                try
                                {
                                    fm.InitialFreq(player, ref ship, ref freq);
                                }
                                finally
                                {
                                    _broker.ReleaseInterface(ref fm);
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
                            PlayerSyncDone(player);
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
                        FirePlayerActionEvent(player, PlayerAction.EnterArena, arena);

                        break;

                    case PlayerState.LeavingArena:
                        FirePlayerActionEvent(player, PlayerAction.LeaveArena, player.Arena);
                        break;

                    case PlayerState.DoArenaSync2:
                        /*if (persist != null && pdata.hasdonegsync)
                            persist.PutPlayer(player, player.Arena, playerSyncDone);
                        else*/
                            PlayerSyncDone(player);
                        pdata.hasdoneasync = false;
                        break;

                    case PlayerState.LeavingZone:
                        if (pdata.hasdonegcallbacks)
                            FirePlayerActionEvent(player, PlayerAction.Disconnect, null);

                        /*if (_persist != null && pdata.hasdonegsync)
                            _persist.PutPlayer(player, null, playerSyncDone);
                        else*/
                            PlayerSyncDone(player);

                        pdata.hasdonegsync = false;
                        break;
                }
            }

            return true;
        }

        private void FirePlayerActionEvent(Player p, PlayerAction action, Arena arena)
        {
            if (p == null)
                return;

            if (arena != null)
                PlayerActionCallback.Fire(arena, p, action, arena);
            else
                PlayerActionCallback.Fire(_broker, p, action, arena);
        }

        private void FailLoginWith(Player p, AuthCode authCode, string text, string logmsg)
        {
            AuthData auth = new AuthData();

            if (p.Type == ClientType.Continuum && !string.IsNullOrWhiteSpace(text))
            {
                auth.Code = AuthCode.CustomText;
                auth.CustomText = text;
            }
            else
            {
                auth.Code = authCode;
            }

            _playerData.WriteLock();
            try
            {
                p.Status = PlayerState.WaitAuth;
            }
            finally
            {
                _playerData.WriteUnlock();
            }

            AuthDone(p, auth);

            _logManager.LogM(LogLevel.Drivel, nameof(Core), "[pid={0}] Login request denied: {1}", p.Id, logmsg);
        }

        private void Packet_Login(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (!(p[_pdkey] is CorePlayerData pdata))
                return;

            if (!p.IsStandard)
            {
                _logManager.LogM(LogLevel.Malicious, nameof(Core), "[pid={0}] Login packet from wrong client type ({1})", p.Id, p.Type);
            }
#if CFG_RELAX_LENGTH_CHECKS
            else if ((p.Type == ClientType.VIE && len < LoginPacket.LengthVIE) 
                || (p.Type == ClientType.Continuum && len < LoginPacket.LengthContinuum))
#else
            else if ((p.Type == ClientType.VIE && len != LoginPacket.LengthVIE)
                || (p.Type == ClientType.Continuum && len != LoginPacket.LengthContinuum))
#endif
            {
                _logManager.LogM(LogLevel.Malicious, nameof(Core), "[pid={0}] Bad login packet length ({1})", p.Id, len);
            }
            else if (p.Status != PlayerState.Connected)
            {
                _logManager.LogM(LogLevel.Malicious, nameof(Core), "[pid={0}] Login request from wrong stage: {1}", p.Id, p.Status);
            }
            else
            {
                LoginPacket pkt = new LoginPacket(data);

#if !CFG_RELAX_LENGTH_CHECKS
                // VIE clients can only have one version. 
                // Continuum clients will need to ask for an update.
                if (p.Type == ClientType.VIE && pkt.CVersion != ClientVersion_VIE)
                {
                    FailLoginWith(p, AuthCode.LockedOut, null, "Bad VIE client version");
                    return;
                }
#endif

                // copy into (per player) storage for use by authenticator
                if (len > 512)
                    len = 512;

                pdata.LoginPacketBuffer = Pool<DataBuffer>.Default.Get();
                Array.Copy(data, pdata.LoginPacketBuffer.Bytes, len);
                pdata.LoginPacketBuffer.NumBytes = len;
                pkt = new LoginPacket(pdata.LoginPacketBuffer.Bytes);

                // name
                CleanupName(pkt.NameSpan); // first, manipulate name as bytes (fewer string object allocations)
                string name = pkt.Name;

                // if nothing could be salvaged from their name, disconnect them
                if (string.IsNullOrWhiteSpace(name))
                {
                    FailLoginWith(p, AuthCode.BadName, "Your player name contains no valid characters", "all invalid chars");
                    return;
                }

                // must start with number or letter
                if (!char.IsLetterOrDigit(name[0]))
                {
                    FailLoginWith(p, AuthCode.BadName, "Your player name must start with a letter or number", "name doesn't start with alphanumeric");
                    return;
                }

                // pass must be nul-terminated
                pkt.PasswordSpan[^1] = 0;

                // fill misc data
                p.MacId = pkt.MacId;
                p.PermId = pkt.D2;

                if (p.Type == ClientType.VIE)
                    p.ClientName = string.Format("<ss/vie client, v. {0}>", pkt.CVersion);
                else if (p.Type == ClientType.Continuum)
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

                _logManager.LogM(LogLevel.Drivel, nameof(Core), "[pid={0}] login request: '{1}'", p.Id, name);
            }

            static void CleanupName(Span<byte> nameSpan)
            {
                // limit name to 20 bytes
                // the last byte must be the nul-terminator
                nameSpan.Slice(19).Fill(0);
                nameSpan = nameSpan.Slice(0, 20);

                // only allow printable characters in names, excluding colon.
                // while we're at it, remove leading, trailing, and series of spaces
                byte c, cc = (byte)' ';
                int s = 0;
                int l = 0;

                while ((c = nameSpan[s++]) != 0)
                {
                    if (c >= 32 && c <= 126 && c != (byte)':')
                    {
                        if (c == (byte)' ' && cc == (byte)' ')
                            continue;

                        nameSpan[l++] = cc = c;
                    }
                }

                // check for a trailing space
                if (l > 0 && nameSpan[l - 1] == (byte)' ')
                    l--;

                nameSpan.Slice(l).Fill(0);
            }
        }

        private void AuthDone(Player p, AuthData auth)
        {
            if (!(p[_pdkey] is CorePlayerData pdata))
                return;

            if (p.Status != PlayerState.WaitAuth)
            {
                _logManager.LogM(LogLevel.Warn, nameof(Core), "[pid={0}] AuthDone called from wrong stage: {1}", p.Id, p.Status);
                return;
            }

            // copy the authdata
            pdata.authdata = auth;

            p.Flags.Authenticated = auth.Authenticated;

            if (auth.Code.AuthIsOK())
            {
                // login succeeded

                // try to locate existing player with the same name
                Player oldp = _playerData.FindPlayer(auth.Name);

                // set new player's name
                p.pkt.Name = auth.SendName;
                p.Name = auth.Name;
                p.pkt.Squad = auth.Squad;
                p.Squad = auth.Squad;

                // make sure we don't have two identical players. if so, do not
                // increment stage yet. we'll do it when the other player leaves
                if (oldp != null && oldp != p)
                {
                    if (!(p[_pdkey] is CorePlayerData oldd))
                        return;

                    _logManager.LogM(LogLevel.Drivel, nameof(Core), "[{0}] player already on, kicking him off (pid {1} replacing {2})", auth.Name, p.Id, oldp.Id);
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
                // if the login didn't succeed status should go to PlayerState.Connected
                // instead of moving forward, and send the login response now, since we won't do it later.
                SendLoginResponse(p);
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

        private void PlayerSyncDone(Player player)
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
                            _logManager.LogM(LogLevel.Warn, nameof(Core), "[oldpid={0}] [newpid={1}] unexpected status when replacing players: {2}", player.Id, replacedBy.Id, replacedBy.Status);
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
                    _logManager.LogM(LogLevel.Warn, nameof(Core), "[pid={0}] player_sync_done called from wrong status: {1}", player.Id, player.Status);
                }
            }
            finally
            {
                _playerData.WriteUnlock();
            }
        }

        private static string GetAuthCodeMessage(AuthCode code)
        {
            return code switch
            {
                AuthCode.OK => "ok",
                AuthCode.NewName => "new user",
                AuthCode.BadPassword => "incorrect password",
                AuthCode.ArenaFull => "arena full",
                AuthCode.LockedOut => "you have been locked out",
                AuthCode.NoPermission => "no permission",
                AuthCode.SpecOnly => "you can spec only",
                AuthCode.TooManyPoints => "you have too many points",
                AuthCode.TooSlow => "too slow (?)",
                AuthCode.NoPermission2 => "no permission (2)",
                AuthCode.NoNewConn => "the server is not accepting new connections",
                AuthCode.BadName => "bad player name",
                AuthCode.OffensiveName => "offensive player name",
                AuthCode.NoScores => "the server is not recordng scores",
                AuthCode.ServerBusy => "the server is busy",
                AuthCode.TooLowUsage => "too low usage",
                AuthCode.AskDemographics => "need demographics",
                AuthCode.TooManyDemo => "too many demo players",
                AuthCode.NoDemo => "no demo players allowed",
                _ => "???",
            };
        }

        private void SendLoginResponse(Player player)
        {
            if (player == null)
                return;

            if (!(player[_pdkey] is CorePlayerData pdata))
                return;

            AuthData auth = pdata.authdata;

            if (auth == null)
            {
                _logManager.LogM(LogLevel.Error, nameof(Core), "Missing authdata for pid {0}", player.Id);
                _playerData.KickPlayer(player);
            }
            else if (player.IsStandard)
            {
                using DataBuffer buffer = Pool<DataBuffer>.Default.Get();
                LoginResponsePacket lr = new LoginResponsePacket(buffer.Bytes);
                lr.Initialize();
                lr.Code = (byte)auth.Code;
                lr.DemoData = auth.DemoData ? (byte)1 : (byte)0;
                lr.NewsChecksum = _map.GetNewsChecksum();
                                         
                if (player.Type == ClientType.Continuum)
                {
                    using (DataBuffer contVersionBuffer = Pool<DataBuffer>.Default.Get())
                    {
                        ContinuumVersionPacket pkt = new ContinuumVersionPacket(contVersionBuffer.Bytes);
                        pkt.Type = (byte)S2CPacketType.ContVersion;
                        pkt.ContVersion = ClientVersion_Cont;
                        pkt.Checksum = _continuumChecksum;

                        _net.SendToOne(player, contVersionBuffer.Bytes, ContinuumVersionPacket.Length, NetSendFlags.Reliable);
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
                        Span<byte> customSpan = stackalloc byte[256];
                        customSpan[0] = (byte)S2CPacketType.LoginText;
                        int bytes = customSpan.Slice(1).WriteNullTerminatedASCII(auth.CustomText.AsSpan(0, Math.Min(auth.CustomText.Length, 254)));
                        _net.SendToOne(player, customSpan.Slice(0, 1 + bytes), NetSendFlags.Reliable);
                    }
                    else
                    {
                        // VIE doesn't understand that packet
                        lr.Code = (byte)AuthCode.LockedOut;
                    }
                }

                _net.SendToOne(player, buffer.Bytes, LoginResponsePacket.Length, NetSendFlags.Reliable);
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
            DefaultAuth(p, lp, lplen, done);   
        }

#endregion

        private void DefaultAuth(Player p, LoginPacket lp, int lplen, AuthDoneDelegate done)
        {
            AuthData auth = new AuthData();

            auth.DemoData = false;
            auth.Code = AuthCode.OK;
            auth.Authenticated = true; //false; // HACK: until I have time to add the AuthFile module

            string name = lp.Name;
            auth.Name = name.Length > 23 ? name.Substring(0, 23) : name;
            auth.SendName = name.Length > 19 ? name.Substring(0, 19) : name;
            auth.Squad = null;

            done(p, auth);
        }

        private bool MainloopTimer_SendKeepAlive()
        {
            if (_net != null)
            {
                _net.SendToArena(null, null, _keepAlive, 1, NetSendFlags.Reliable);
            }
            return true;
        }
    }
}

using CommunityToolkit.HighPerformance.Buffers;
using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    /// <summary>
    /// This module handles transitioning <see cref="Player"/>s through their various <see cref="PlayerState"/>s.
    /// That is, it is responsible for moving a player through it's life-cycle: from getting authenticated by server, entering/leaving arenas, all the way to leaving the zone.
    /// This is done through an intricate series of handoffs between core modules based on <see cref="Player.Status"/>.
    /// <para>
    /// This includes handling of the login process. The logic included in this module calls the registered authentication provider (<see cref="IAuth"/>)
    /// and handles communicating the results of it to the client.  Also, this module provides a default implementation of <see cref="IAuth"/> which 
    /// allows all logins to succeed as being unauthenticated.
    /// </para>
    /// </summary>
    [CoreModuleInfo]
    public class Core : IAsyncModule, IModuleLoaderAware, IAuth
    {
        // required dependencies
        private readonly IComponentBroker _broker;
        private readonly IArenaManager _arenaManager;
        private readonly IArenaManagerInternal _arenaManagerInternal;
        private readonly ICapabilityManager _capabilityManager;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IMainloop _mainloop;
        private readonly IMainloopTimer _mainloopTimer;
        private readonly IMapNewsDownload _mapNewsDownload;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;

        // optional dependencies (available on Load)
        private IChatNetwork? _chatNetwork;
        private INetwork? _network;

        // optional dependencies (available on PostLoad)
        private IChat? _chat;
        private IPersistExecutor? _persistExecutor;
        private IScoreStats? _scoreStats;

        private InterfaceRegistrationToken<IAuth>? _iAuthToken;

        private readonly DefaultObjectPool<AuthRequest> _authRequestPool = new(new DefaultPooledObjectPolicy<AuthRequest>(), Constants.TargetPlayerCount);

        private PlayerDataKey<PlayerData> _pdkey;

        private const ushort ClientVersion_VIE = 134;
        private const ushort ClientVersion_Cont = 40;

        private const string ContinuumExeFile = "clients/Continuum.exe";
        private const string ContinuumChecksumFile = "scrty";

        private uint _continuumChecksum;
        private uint _codeChecksum;

        private readonly Action<Player> _authDone;
        private readonly Action<Player> _playerSyncDone;

        internal Core(
            IComponentBroker broker,
            IArenaManager arenaManager,
            IArenaManagerInternal arenaManagerInternal,
            ICapabilityManager capabilityManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloop mainloop,
            IMainloopTimer mainloopTimer,
            IMapNewsDownload mapNewsDownload,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _arenaManagerInternal = arenaManagerInternal ?? throw new ArgumentNullException(nameof(arenaManagerInternal));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _mapNewsDownload = mapNewsDownload ?? throw new ArgumentNullException(nameof(mapNewsDownload));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _authDone = AuthDone;
            _playerSyncDone = PlayerSyncDone;
        }

        #region Module Members

        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _network = broker.GetInterface<INetwork>();
            _chatNetwork = broker.GetInterface<IChatNetwork>();

            if (_network is null && _chatNetwork is null)
            {
                _logManager.LogM(LogLevel.Error, nameof(Core), $"At least one network dependency is required ({nameof(INetwork)} and/or {nameof(IChatNetwork)}.");
                return false;
            }

            _continuumChecksum = await GetChecksumAsync(ContinuumExeFile).ConfigureAwait(false);
            _codeChecksum = await GetUInt32Async(ContinuumChecksumFile, 4).ConfigureAwait(false);

            _pdkey = _playerData.AllocatePlayerData<PlayerData>();

            NewPlayerCallback.Register(broker, Callback_NewPlayer);

            _network?.AddPacket(C2SPacketType.Login, Packet_Login);
            _network?.AddPacket(C2SPacketType.ContLogin, Packet_Login);

            _chatNetwork?.AddHandler("LOGIN", ChatHandler_Login);

            _mainloopTimer.SetTimer(MainloopTimer_ProcessPlayerStates, 100, 100, null);
            _mainloopTimer.SetTimer(MainloopTimer_SendKeepAlive, 5000, 5000, null); // every 5 seconds

            // register default interface which may be replaced later
            _iAuthToken = broker.RegisterInterface<IAuth>(this);

            return true;
        }

        void IModuleLoaderAware.PostLoad(IComponentBroker broker)
        {
            _chat = broker.GetInterface<IChat>();
            _persistExecutor = broker.GetInterface<IPersistExecutor>();
            _scoreStats = broker.GetInterface<IScoreStats>();
        }

        void IModuleLoaderAware.PreUnload(IComponentBroker broker)
        {
            if (_chat is not null)
            {
                broker.ReleaseInterface(ref _chat);
            }

            if (_persistExecutor != null)
            {
                broker.ReleaseInterface(ref _persistExecutor);
            }

            if (_scoreStats != null)
            {
                broker.ReleaseInterface(ref _scoreStats);
            }
        }

        Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (broker.UnregisterInterface(ref _iAuthToken) != 0)
                return Task.FromResult(false);

            _mainloopTimer.ClearTimer(MainloopTimer_SendKeepAlive, null);
            _mainloopTimer.ClearTimer(MainloopTimer_ProcessPlayerStates, null);

            _network?.RemovePacket(C2SPacketType.Login, Packet_Login);
            _network?.RemovePacket(C2SPacketType.ContLogin, Packet_Login);

            _chatNetwork?.RemoveHandler("LOGIN", ChatHandler_Login);

            NewPlayerCallback.Unregister(broker, Callback_NewPlayer);

            _playerData.FreePlayerData(ref _pdkey);

            if (_network is not null)
            {
                broker.ReleaseInterface(ref _network);
            }

            if (_chatNetwork is not null)
            {
                broker.ReleaseInterface(ref _chatNetwork);
            }

            return Task.FromResult(true);
        }

        #endregion

        #region IAuth Members

        void IAuth.Authenticate(IAuthRequest authRequest)
        {
            // Default Auth - allows everyone in, unauthenticated
            IAuthResult result = authRequest.Result;
            result.DemoData = false;
            result.Code = AuthCode.OK;
            result.Authenticated = false;

            ref readonly LoginPacket lp = ref authRequest.LoginPacket;

            ReadOnlySpan<byte> nameBytes = ((ReadOnlySpan<byte>)lp.Name).SliceNullTerminated();
            Span<char> name = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(nameBytes)];
            int decodedCharCount = StringUtils.DefaultEncoding.GetChars(nameBytes, name);
            Debug.Assert(name.Length == decodedCharCount);

            result.SetName(name);
            result.SetSendName(name);
            result.SetSquad("");

            authRequest.Done();
        }

        #endregion

        private void Callback_NewPlayer(Player player, bool isNew)
        {
            if (player is null || !player.TryGetExtraData(_pdkey, out PlayerData? playerData))
                return;

            if (playerData.AuthRequest is not null)
            {
                _authRequestPool.Return(playerData.AuthRequest);
                playerData.AuthRequest = null;
            }
        }

        #region Timers

        private readonly struct PlayerStateChange(Player player, PlayerState oldStatus)
        {
            public readonly Player Player = player;
            public readonly PlayerState OldStatus = oldStatus;

            public void Deconstruct(out Player player, out PlayerState oldStatus)
            {
                player = Player;
                oldStatus = OldStatus;
            }
        }

        /// <summary>
        /// For <see cref="MainloopTimer_ProcessPlayerStates"/> ONLY.
        /// This list holds pending actions while processing the player list.
        /// </summary>
        private readonly List<PlayerStateChange> _actionsList = new(Constants.TargetPlayerCount);

        private bool MainloopTimer_ProcessPlayerStates()
        {
            Span<char> arenaName = stackalloc char[Constants.MaxArenaNameLength];

            _playerData.WriteLock();

            try
            {
                PlayerState ns;
                foreach (Player player in _playerData.Players)
                {
                    PlayerState oldStatus = player.Status;
                    switch (oldStatus)
                    {
                        // For all of these states, there's nothing to do in this loop.
                        case PlayerState.Uninitialized:
                        case PlayerState.WaitAuth:
                        case PlayerState.WaitGlobalSync1:
                        case PlayerState.WaitGlobalSync2:
                        case PlayerState.WaitArenaSync1:
                        case PlayerState.WaitArenaSync2:
                        case PlayerState.Playing:
                        case PlayerState.TimeWait:
                            continue;

                        // This is an interesting state: this function is responsible for some transitions away from PlayerState.LoggedIn.
                        // We also do the Player.WhenLoggedIn transition if the player is just connected and not logged in yet.
                        case PlayerState.Connected:
                        case PlayerState.LoggedIn:
                            // at this point, the player can't have an arena
                            player.Arena = null;

                            // check if the player's arena is ready.
                            // LOCK: we don't grab the arena status lock because it
                            // doesn't matter if we miss it this time around
                            Arena? newArena = player.NewArena;
                            if (newArena is not null && newArena.Status == ArenaState.Running)
                            {
                                player.NewArena = null;

                                bool isAuthorized;
                                StringBuilder errorMessage = _objectPoolManager.StringBuilderPool.Get();
                                try
                                {
                                    isAuthorized = IsPlayerAuthorizedToEnterArena(player, newArena, errorMessage);

                                    if (!isAuthorized)
                                    {
                                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                                        try
                                        {
                                            sb.Append($"You don't have permission to enter arena '{newArena.Name}'.");

                                            if (errorMessage.Length > 0)
                                            {
                                                sb.Append(' ');
                                                sb.Append(errorMessage);
                                            }

                                            _chat?.SendMessage(player, sb);
                                        }
                                        finally
                                        {
                                            _objectPoolManager.StringBuilderPool.Return(sb);
                                        }
                                    }
                                }
                                finally
                                {
                                    _objectPoolManager.StringBuilderPool.Return(errorMessage);
                                }

                                if (!isAuthorized)
                                {
                                    IArenaPlace? arenaPlace = _broker.GetInterface<IArenaPlace>();
                                    if (arenaPlace is not null)
                                    {
                                        try
                                        {
                                            bool ok = arenaPlace.TryPlace(player, arenaName, out int charsWritten, out _, out _);
                                            if (ok)
                                            {
                                                Arena? arena = _arenaManager.FindArena(arenaName);
                                                if (arena is not null)
                                                {
                                                    if (!IsPlayerAuthorizedToEnterArena(player, arena, null))
                                                    {
                                                        // The player isn't authorized to enter the placed arena.
                                                        // This will prevent an infinite loop of being sent to an arena that a player isn't authorized to enter.
                                                        ok = false;
                                                    }
                                                }
                                            }

                                            if (ok)
                                            {
                                                _logManager.LogP(LogLevel.Info, nameof(Core), player, $"Redirected from arena '{newArena.Name}' to '{arenaName}'.");
                                                _arenaManager.SendToArena(player, arenaName[..charsWritten], 0, 0);
                                            }
                                        }
                                        finally
                                        {
                                            _broker.ReleaseInterface(ref arenaPlace);
                                        }
                                    }
                                }
                                else
                                {
                                    player.Arena = newArena;
                                    player.Status = PlayerState.DoFreqAndArenaSync;
                                }
                            }

                            // Check Player.WhenLoggedIn.
                            // This is used to move players to PlayerState.LeavingZone once various things are completed.
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
                        case PlayerState.DoGlobalCallbacks: ns = PlayerState.WaitConnectHolds; break;

                        case PlayerState.WaitConnectHolds:
                            if (player.Holds == 0)
                            {
                                ns = PlayerState.SendLoginResponse;
                                break;
                            }

                            continue;

                        case PlayerState.SendLoginResponse: ns = PlayerState.LoggedIn; break;
                        case PlayerState.DoFreqAndArenaSync: ns = PlayerState.WaitArenaSync1; break;
                        case PlayerState.ArenaRespAndCBS: ns = PlayerState.Playing; break;
                        case PlayerState.LeavingArena: ns = PlayerState.DoArenaSync2; break;
                        case PlayerState.DoArenaSync2: ns = PlayerState.WaitArenaSync2; break;
                        case PlayerState.LeavingZone: ns = PlayerState.WaitDisconnectHolds; break;

                        case PlayerState.WaitDisconnectHolds:
                            if (player.Holds == 0)
                            {
                                ns = PlayerState.WaitGlobalSync2;
                                break;
                            }

                            continue;

                        default: // catch any other state
                            _logManager.LogM(LogLevel.Error, nameof(Core), $"[pid={player.Id}] Internal error: Unknown player status {oldStatus}.");
                            continue;
                    }

                    player.Status = ns;

                    // add this player to the pending actions list, to be run when we release the status lock.
                    _actionsList.Add(new PlayerStateChange(player, oldStatus));
                }
            }
            finally
            {
                _playerData.WriteUnlock();
            }

            if (_actionsList.Count == 0)
                return true;

            foreach ((Player player, PlayerState oldStatus) in _actionsList)
            {
                if (!player.TryGetExtraData(_pdkey, out PlayerData? playerData))
                    continue;

                switch (oldStatus)
                {
                    case PlayerState.NeedAuth:
                        {
                            IAuth? auth = _broker.GetInterface<IAuth>();
                            if (auth is null)
                            {
                                _logManager.LogP(LogLevel.Warn, nameof(Core), player, "Can't authenticate player. IAuth implementation not found.");
                                _playerData.KickPlayer(player);
                                break;
                            }

                            try
                            {
                                if (playerData.AuthRequest is null)
                                {
                                    _logManager.LogP(LogLevel.Warn, nameof(Core), player, "Can't authenticate player. Missing AuthRequest.");
                                    _playerData.KickPlayer(player);
                                    break;
                                }

                                if (playerData.AuthRequest.LoginBytes.IsEmpty)
                                {
                                    _logManager.LogP(LogLevel.Warn, nameof(Core), player, "Can't authenticate player. AuthRequest is missing LoginBytes.");
                                    _playerData.KickPlayer(player);
                                    break;
                                }

                                _logManager.LogP(LogLevel.Drivel, nameof(Core), player, $"Authenticating with '{auth.GetType()}'.");
                                auth.Authenticate(playerData.AuthRequest);
                            }
                            finally
                            {
                                _broker.ReleaseInterface(ref auth);
                            }
                        }
                        break;

                    case PlayerState.NeedGlobalSync:
                        if (_persistExecutor != null)
                            _persistExecutor.GetPlayer(player, null, _playerSyncDone);
                        else
                            PlayerSyncDone(player);

                        playerData.HasDoneGlobalSync = true;
                        break;

                    case PlayerState.DoGlobalCallbacks:
                        FirePlayerActionEvent(player, PlayerAction.Connect, null);
                        playerData.HasDoneGlobalCallbacks = true;
                        break;

                    case PlayerState.SendLoginResponse:
                        SendLoginResponse(player);
                        _logManager.LogM(LogLevel.Info, nameof(Core), $"[{player.Name}] [pid={player.Id}] Player logged in from ip={player.IPAddress} macid={player.MacId:X}.");
                        break;

                    case PlayerState.DoFreqAndArenaSync:
                        // the arena will be fully loaded here
                        ShipType requestedShip = player.Ship;
                        player.Ship = (ShipType)(-1);
                        player.Freq = -1;

                        // do pre-callbacks
                        FirePlayerActionEvent(player, PlayerAction.PreEnterArena, player.Arena);

                        // get a freq
                        if ((int)player.Ship == -1 || player.Freq == -1)
                        {
                            short freq = 0;

                            // If the arena has a manager, use it.
                            IFreqManager? fm = player.Arena!.GetInterface<IFreqManager>();
                            if (fm != null)
                            {
                                try
                                {
                                    fm.Initial(player, ref requestedShip, ref freq);
                                }
                                finally
                                {
                                    _broker.ReleaseInterface(ref fm);
                                }
                            }

                            // set results back
                            player.Ship = requestedShip;
                            player.Freq = freq;
                        }

                        // sync scores
                        if (_persistExecutor != null)
                            _persistExecutor.GetPlayer(player, player.Arena, _playerSyncDone);
                        else
                            PlayerSyncDone(player);

                        playerData.HasDoneArenaSync = true;
                        break;

                    case PlayerState.ArenaRespAndCBS:
                        // Refresh scores in the PlayerEntering packets (Player.Packet).
                        // These packets will be sent later in the SendArenaResponse method call after this.
                        if (_scoreStats != null)
                        {
                            // At this point, the player's stats should be loaded into the stats module since _persist.GetPlayer(...) was called earlier.
                            // Try to load scores into the player's PlayerEntering packet.
                            _scoreStats.GetScores(player, out int killPoints, out int flagPoints, out ushort kills, out ushort deaths);

                            player.Packet.KillPoints = killPoints;
                            player.Packet.FlagPoints = flagPoints;
                            player.Packet.Wins = kills;
                            player.Packet.Losses = deaths;

                            // Refresh scores for other players in the arena.
                            // This ensures that the latest scores will be in the PlayerEntering packets of all the player's already in the arena.
                            _scoreStats.SendUpdates(player.Arena, player);
                        }

                        _arenaManagerInternal.SendArenaResponse(player);
                        player.Flags.SentPositionPacket = false;
                        player.Flags.SentWeaponPacket = false;

                        FirePlayerActionEvent(player, PlayerAction.EnterArena, player.Arena);
                        break;

                    case PlayerState.LeavingArena:
                        FirePlayerActionEvent(player, PlayerAction.LeaveArena, player.Arena);
                        break;

                    case PlayerState.DoArenaSync2:
                        if (_persistExecutor != null && playerData.HasDoneArenaSync)
                            _persistExecutor.PutPlayer(player, player.Arena, _playerSyncDone);
                        else
                            PlayerSyncDone(player);

                        playerData.HasDoneArenaSync = false;
                        break;

                    case PlayerState.LeavingZone:
                        if (playerData.HasDoneGlobalCallbacks)
                            FirePlayerActionEvent(player, PlayerAction.Disconnect, null);

                        break;

                    case PlayerState.WaitDisconnectHolds:
                        if (_persistExecutor != null && playerData.HasDoneGlobalSync)
                            _persistExecutor.PutPlayer(player, null, _playerSyncDone);
                        else
                            PlayerSyncDone(player);

                        playerData.HasDoneGlobalSync = false;
                        break;
                }
            }

            _actionsList.Clear();
            return true;

            // local function that wraps calling the IArenaAuthorizationAdvisor
            static bool IsPlayerAuthorizedToEnterArena(Player player, Arena arena, StringBuilder? errorMessage)
            {
                var advisors = arena.GetAdvisors<IArenaAuthorizationAdvisor>();
                if (!advisors.IsEmpty)
                {
                    foreach (var advisor in advisors)
                    {
                        if (!advisor.IsAuthorizedToEnter(player, arena, errorMessage))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
        }

        private bool MainloopTimer_SendKeepAlive()
        {
            if (_network != null)
            {
                ReadOnlySpan<byte> keepAlive = stackalloc byte[1] { (byte)S2CPacketType.KeepAlive };
                _network.SendToArena(null, null, keepAlive, NetSendFlags.Reliable);
            }

            return true;
        }

        #endregion

        private void Packet_Login(Player player, Span<byte> data, NetReceiveFlags flags)
        {
            if (player is null || !player.TryGetExtraData(_pdkey, out PlayerData? playerData))
                return;

            if (!player.IsStandard)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Core), player, $"Login packet from wrong client type ({player.Type}).");
            }
#if CFG_RELAX_LENGTH_CHECKS
            else if ((p.Type == ClientType.VIE && data.Length < LoginPacket.LengthVIE) 
                || (p.Type == ClientType.Continuum && data.Length < LoginPacket.LengthContinuum))
#else
            else if ((player.Type == ClientType.VIE && data.Length != LoginPacket.VIELength)
                || (player.Type == ClientType.Continuum && data.Length != LoginPacket.ContinuumLength))
#endif
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Core), player, $"Bad login packet (length={data.Length}).");
            }
            else if (player.Status != PlayerState.Connected)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Core), player, $"Login request from wrong stage: {player.Status}.");
            }
            else
            {
                ref LoginPacket pkt = ref MemoryMarshal.AsRef<LoginPacket>(data);

#if !CFG_RELAX_LENGTH_CHECKS
                // VIE clients can only have one version. 
                // Continuum clients will need to ask for an update.
                if (player.Type == ClientType.VIE && pkt.CVersion != ClientVersion_VIE)
                {
                    FailLoginWith(player, AuthCode.LockedOut, null, "Bad VIE client version");
                    return;
                }
#endif

                // copy into (per player) storage for use by authenticator
                if (data.Length > Constants.MaxPacket)
                {
                    data = data[..Constants.MaxPacket];
                }

                if (playerData.AuthRequest is not null)
                {
                    _authRequestPool.Return(playerData.AuthRequest);
                }

                playerData.AuthRequest = _authRequestPool.Get();
                playerData.AuthRequest.SetRequestInfo(player, data, _authDone);

                pkt = ref MemoryMarshal.AsRef<LoginPacket>(playerData.AuthRequest.LoginBytes);

                // name
                ReadOnlySpan<byte> nameBytes = ((ReadOnlySpan<byte>)pkt.Name).SliceNullTerminated();
                Span<char> name = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(nameBytes)];
                int decodedCharCount = StringUtils.DefaultEncoding.GetChars(nameBytes, name);
                Debug.Assert(name.Length == decodedCharCount);
                Span<char> cleanName = stackalloc char[name.Length];
                CleanupPlayerName(name, ref cleanName);

                if (cleanName.IsEmpty)
                {
                    // Nothing could be salvaged from the provided name.
                    FailLoginWith(player, AuthCode.BadName, "Your player name contains no valid characters.", "all invalid chars");
                    return;
                }

                StringUtils.WriteNullPaddedString(pkt.Name, cleanName, false);

                // pass must be nul-terminated
                pkt.Password[^1] = 0;

                // fill misc data
                player.MacId = pkt.MacId;
                player.PermId = pkt.D2;

                if (player.Type == ClientType.VIE)
                    player.ClientName = $"<ss/vie client, v. {pkt.CVersion}>";
                else if (player.Type == ClientType.Continuum)
                    player.ClientName = $"<continuum, v. {pkt.CVersion}>";

                // set up status
                _playerData.WriteLock();
                try
                {
                    player.Status = PlayerState.NeedAuth;
                }
                finally
                {
                    _playerData.WriteUnlock();
                }

                _logManager.LogP(LogLevel.Drivel, nameof(Core), player, $"Login request: '{name}'.");
            }
        }

        private void ChatHandler_Login(Player player, ReadOnlySpan<char> message)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdkey, out PlayerData? playerData))
            {
                _chatNetwork!.SendToOne(player, "LOGINBAD:Internal Server Error");
                return;
            }

            if (player.Status != PlayerState.Connected)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Core), player, $"Login request from wrong stage: {player.Status}.");
                return;
            }

            ReadOnlySpan<char> versionClient = message.GetToken(':', out ReadOnlySpan<char> remaining);
            if (versionClient.IsEmpty)
            {
                _chatNetwork!.SendToOne(player, "LOGINBAD:Bad Request");
                return;
            }

            ReadOnlySpan<char> versionSpan = versionClient.GetToken(';', out ReadOnlySpan<char> clientInfo);
            if (versionClient.IsEmpty)
            {
                _chatNetwork!.SendToOne(player, "LOGINBAD:Bad Request");
                return;
            }

            if (!ushort.TryParse(versionSpan, out ushort version))
            {
                _chatNetwork!.SendToOne(player, "LOGINBAD:Bad Request");
                return;
            }

            if (!clientInfo.IsEmpty)
                clientInfo = clientInfo[1..]; // skip the ;

            ReadOnlySpan<char> name = remaining.GetToken(':', out remaining);
            if (name.IsEmpty || remaining.IsEmpty)
            {
                _chatNetwork!.SendToOne(player, "LOGINBAD:Bad Request");
                return;
            }

            // Cleanup the name.
            Span<char> cleanName = stackalloc char[name.Length];
            CleanupPlayerName(name, ref cleanName);
            if (cleanName.IsEmpty)
            {
                _chatNetwork!.SendToOne(player, $"LOGINBAD:{GetAuthCodeMessage(AuthCode.BadName)}");
                return;
            }

            ReadOnlySpan<char> password = remaining[1..]; // skip the :
            if (remaining.IsEmpty)
            {
                _chatNetwork!.SendToOne(player, "LOGINBAD:Bad Request");
                return;
            }

            player.ClientName = $"chat: {clientInfo}"; // TODO: string allocation

            // Build a login packet.
            LoginPacket loginPacket = new();
            loginPacket.CVersion = version;

            if (StringUtils.DefaultEncoding.GetByteCount(cleanName) > LoginPacket.NameInlineArray.Length)
            {
                _chatNetwork!.SendToOne(player, "LOGINBAD:Bad Request");
                return;
            }

            StringUtils.WriteNullPaddedString(loginPacket.Name, cleanName, false);
            loginPacket.Name.Clear();
            StringUtils.DefaultEncoding.GetBytes(cleanName, loginPacket.Name);

            if (StringUtils.DefaultEncoding.GetByteCount(password) > LoginPacket.PasswordInlineArray.Length)
            {
                _chatNetwork!.SendToOne(player, "LOGINBAD:Bad Request");
                return;
            }

            loginPacket.Password.Clear();
            StringUtils.DefaultEncoding.GetBytes(password, loginPacket.Password);

            // Add an auth request.
            if (playerData.AuthRequest is not null)
            {
                _authRequestPool.Return(playerData.AuthRequest);
            }

            playerData.AuthRequest = _authRequestPool.Get();
            playerData.AuthRequest.SetRequestInfo(player, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref loginPacket, 1)), _authDone);

            // Set the player's status.
            _playerData.WriteLock();

            try
            {
                player.Status = PlayerState.NeedAuth;
            }
            finally
            {
                _playerData.WriteUnlock();
            }

            _logManager.LogP(LogLevel.Drivel, nameof(Core), player, $"Login request: '{cleanName}'.");
        }

        /// <summary>
        /// Cleans up a player's name.
        /// </summary>
        /// <remarks>
        /// Valid characters are copied from <paramref name="name"/> to <paramref name="cleanName"/>.
        /// <list type="bullet">
        /// <item>Only allow printable ASCII characters, except colon (colon is used to delimit the target in private messages, and also used to delimit in the chat protocol).</item>
        /// <item>The first character must be a letter or digit.</item>
        /// <item>No leading or trailing spaces.</item>
        /// <item>No consecutive spaces.</item>
        /// </list>
        /// </remarks>
        /// <param name="name">The source.</param>
        /// <param name="cleanName">The destination.</param>
        /// <exception cref="ArgumentException">The length of <paramref name="cleanName"/> was less than the length of <paramref name="name"/>.</exception>
        private static void CleanupPlayerName(ReadOnlySpan<char> name, ref Span<char> cleanName)
        {
            if (name.Length > cleanName.Length)
                throw new ArgumentException(paramName: nameof(cleanName), message: "The length must be greater than or equal to the source length.");

            int cleanIndex = 0;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (StringUtils.IsAsciiPrintable(c) // printable ASCII character (extended ASCII excluded on purpose)
                    && c != ':' // excluding colon
                    && (cleanIndex > 0 || char.IsLetter(c) || char.IsDigit(c)) // first character must be a letter or digit
                    && (!char.IsWhiteSpace(c) || (cleanIndex > 0 && !char.IsWhiteSpace(cleanName[cleanIndex - 1])))) // no leading or consecutive spaces
                {
                    cleanName[cleanIndex++] = c;
                }
            }

            cleanName = cleanName[..cleanIndex].TrimEnd(); // no trailing spaces
        }

        private void FailLoginWith(Player player, AuthCode authCode, string? text, string logmsg)
        {
            if (player == null)
                return;

            // Calling this method means we're bypassing PlayerState.NeedAuth, so do some cleanup.
            if (!player.TryGetExtraData(_pdkey, out PlayerData? playerData))
                return;

            playerData.AuthRequest ??= _authRequestPool.Get();

            AuthResult authResult = playerData.AuthRequest.Result;

            if (player.Type == ClientType.Continuum && !string.IsNullOrWhiteSpace(text))
            {
                authResult.Code = AuthCode.CustomText;
                authResult.SetCustomText(text);
            }
            else
            {
                authResult.Code = authCode;
            }

            _playerData.WriteLock();

            try
            {
                player.Status = PlayerState.WaitAuth;
            }
            finally
            {
                _playerData.WriteUnlock();
            }

            AuthDone(player);

            _logManager.LogM(LogLevel.Drivel, nameof(Core), $"[pid={player.Id}] Login request denied: {logmsg}.");
        }

        private void AuthDone(Player player)
        {
            if (player is null)
                return;

            // Ensure this is done on the mainloop thread (just in case an authentication module calls it from a different thread).
            if (!_mainloop.IsMainloop)
            {
                _mainloop.QueueMainWorkItem(_authDone, player);
                return;
            }

            if (!player.TryGetExtraData(_pdkey, out PlayerData? playerData))
                return;

            if (player.Status != PlayerState.WaitAuth)
            {
                _logManager.LogM(LogLevel.Warn, nameof(Core), $"[pid={player.Id}] {nameof(AuthDone)} called from wrong stage: {player.Status}");
                return;
            }

            AuthResult authResult = playerData.AuthRequest!.Result;
            if (authResult.Code is null)
            {
                // Getting here means an authentication module is malfunctioning.
                _logManager.LogP(LogLevel.Error, nameof(Core), player, $"{nameof(AuthDone)} called but the AuthCode was not set.");

                authResult.Code = AuthCode.CustomText;
                authResult.SetCustomText("Internal Server Error");
            }

            bool loginSuccess = authResult.Code.Value.IsOK();
            player.Flags.Authenticated = loginSuccess && authResult.Authenticated;

            if (loginSuccess)
            {
                // Login succeeded

                // Try to locate an existing player with the same name.
                Player? oldPlayer = _playerData.FindPlayer(authResult.Name);

                // Set new player's name. 
                player.Packet.Name.Set(authResult.SendName); // this can truncate
                player.Name = StringPool.Shared.GetOrAdd(Truncate(authResult.Name, Constants.MaxPlayerNameLength)); // TODO: if SendName != Name, then wouldn't that break remote chat messages?
                player.Packet.Squad.Set(authResult.Squad); // this can truncate
                player.Squad = StringPool.Shared.GetOrAdd(Truncate(authResult.Squad, Constants.MaxSquadNameLength));

                // Make sure we don't have two identical players.
                // If so, do not increment stage yet. We'll do it when the other player leaves.
                if (oldPlayer != null && oldPlayer != player)
                {
                    if (!oldPlayer.TryGetExtraData(_pdkey, out PlayerData? oldPlayerData))
                        return;

                    _logManager.LogM(LogLevel.Drivel, nameof(Core), $"[{authResult.Name}] Player already on, kicking him off (pid {player.Id} replacing {oldPlayer.Id}).");
                    oldPlayerData.ReplacedBy = player;
                    _playerData.KickPlayer(oldPlayer);
                }
                else
                {
                    // Increment the stage.
                    _playerData.WriteLock();
                    try
                    {
                        player.Status = PlayerState.NeedGlobalSync;
                    }
                    finally
                    {
                        _playerData.WriteUnlock();
                    }
                }
            }
            else
            {
                // If the login didn't succeed, the status should go to PlayerState.Connected instead of moving forward,
                // and the login response should be sent now, since we won't do it later.
                SendLoginResponse(player);

                _playerData.WriteLock();
                try
                {
                    player.Status = PlayerState.Connected;
                }
                finally
                {
                    _playerData.WriteUnlock();
                }
            }

            static ReadOnlySpan<char> Truncate(ReadOnlySpan<char> value, int maxLength)
            {
                if (value.Length > maxLength)
                {
                    return value[..maxLength];
                }

                return value;
            }
        }

        private void SendLoginResponse(Player player)
        {
            if (player == null)
                return;

            if (!player.TryGetExtraData(_pdkey, out PlayerData? playerData))
                return;

            if (playerData.AuthRequest is null)
            {
                _logManager.LogP(LogLevel.Error, nameof(Core), player, "Can't send login response (no AuthRequest).");
                _playerData.KickPlayer(player);
                return;
            }

            AuthResult authResult = playerData.AuthRequest.Result;

            try
            {
                if (player.IsStandard)
                {
                    if (authResult.Code is null)
                    {
                        _logManager.LogP(LogLevel.Error, nameof(Core), player, "Can't send login response (no AuthResult.AuthCode).");
                        _playerData.KickPlayer(player);
                        return;
                    }

                    S2C_LoginResponse lr = new();
                    lr.Code = (byte)authResult.Code;
                    lr.DemoData = authResult.DemoData ? (byte)1 : (byte)0;
                    lr.NewsChecksum = _mapNewsDownload.GetNewsChecksum();

                    if (player.Type == ClientType.Continuum)
                    {
                        S2C_ContinuumVersion pkt = new(ClientVersion_Cont, _continuumChecksum);
                        _network!.SendToOne(player, ref pkt, NetSendFlags.Reliable);

                        lr.ExeChecksum = _continuumChecksum;
                        lr.CodeChecksum = _codeChecksum;
                    }
                    else
                    {
                        // old VIE exe checksums
                        lr.ExeChecksum = 0xF1429CE8;
                        lr.CodeChecksum = 0x281CC948;
                    }

                    if (_capabilityManager != null && _capabilityManager.HasCapability(player, Constants.Capabilities.SeePrivFreq))
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
                            int bytes = customSpan[1..].WriteNullTerminatedString(authResult.CustomText.TruncateForEncodedByteLimit(254));
                            _network!.SendToOne(player, customSpan[..(1 + bytes)], NetSendFlags.Reliable);
                        }
                        else
                        {
                            // VIE doesn't understand that packet
                            lr.Code = (byte)AuthCode.LockedOut;
                        }
                    }

                    _network!.SendToOne(player, ref lr, NetSendFlags.Reliable);
                }
                else if (player.IsChat)
                {
                    if (authResult.Code is null)
                    {
                        _logManager.LogP(LogLevel.Error, nameof(Core), player, "Can't send login response (no AuthResult.AuthCode).");
                        _chatNetwork!.SendToOne(player, "LOGINBAD:Internal Server Error");
                        _playerData.KickPlayer(player);
                    }
                    else if (authResult.Code.Value.IsOK())
                    {
                        _chatNetwork!.SendToOne(player, $"LOGINOK:{player.Name}");
                    }
                    else if (authResult.Code.Value == AuthCode.CustomText)
                    {
                        _chatNetwork!.SendToOne(player, $"LOGINBAD:{authResult.CustomText}");
                    }
                    else
                    {
                        _chatNetwork!.SendToOne(player, $"LOGINBAD:{GetAuthCodeMessage(authResult.Code.Value)}");
                    }
                }
            }
            finally
            {
                _authRequestPool.Return(playerData.AuthRequest);
                playerData.AuthRequest = null;
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
                    if (!player.TryGetExtraData(_pdkey, out PlayerData? pdata))
                        return;

                    Player? replacedBy = pdata.ReplacedBy;
                    if (replacedBy != null)
                    {
                        if (replacedBy.Status != PlayerState.WaitAuth)
                        {
                            _logManager.LogM(LogLevel.Warn, nameof(Core), $"[oldpid={player.Id}] [newpid={replacedBy.Id}] Unexpected status when replacing players: {replacedBy.Status}.");
                        }
                        else
                        {
                            replacedBy.Status = PlayerState.NeedGlobalSync;
                            pdata.ReplacedBy = null;
                        }
                    }

                    player.Status = PlayerState.TimeWait;
                }
                else
                {
                    _logManager.LogM(LogLevel.Warn, nameof(Core), $"[pid={player.Id}] Player_sync_done called from wrong status: {player.Status}.");
                }
            }
            finally
            {
                _playerData.WriteUnlock();
            }
        }

        private void FirePlayerActionEvent(Player player, PlayerAction action, Arena? arena)
        {
            if (player == null)
                return;

            PlayerActionCallback.Fire(arena ?? _broker, player, action, arena);
        }

        private static string GetAuthCodeMessage(AuthCode code) => code switch
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
            AuthCode.NoScores => "the server is not recording scores",
            AuthCode.ServerBusy => "the server is busy",
            AuthCode.TooLowUsage => "too low usage",
            AuthCode.AskDemographics => "need demographics",
            AuthCode.TooManyDemo => "too many demo players",
            AuthCode.NoDemo => "no demo players allowed",
            _ => "???",
        };

        private async Task<uint> GetChecksumAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Cannot be null or white-space.", nameof(path));

            try
            {
                await using FileStream fs = await Task.Factory.StartNew(
                    static (obj) => new FileStream((string)obj!, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true),
                    path).ConfigureAwait(false);

                Crc32 crc32 = _objectPoolManager.Crc32Pool.Get();

                try
                {
                    await crc32.AppendAsync(fs).ConfigureAwait(false);
                    return crc32.GetCurrentHashAsUInt32();
                }
                finally
                {
                    _objectPoolManager.Crc32Pool.Return(crc32);
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(Core), $"Error getting checksum to '{path}'. {ex.Message}");
                return uint.MaxValue;
            }
        }

        private async Task<uint> GetUInt32Async(string path, int offset)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Cannot be null or white-space.", nameof(path));

            try
            {
                await using FileStream fs = await Task.Factory.StartNew(
                    static (obj) => new FileStream((string)obj!, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true),
                    path).ConfigureAwait(false);

                fs.Seek(offset, SeekOrigin.Begin);

                byte[] buffer = ArrayPool<byte>.Shared.Rent(4);
                try
                {
                    await fs.ReadExactlyAsync(buffer, 0, 4).ConfigureAwait(false);
                    return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(Core), $"Error getting UInt32 from '{path}' at offset {offset}. {ex.Message}");
                return uint.MaxValue;
            }
        }

        #region Helper types

        private class PlayerData : IResettable
        {
            public AuthRequest? AuthRequest;
            public Player? ReplacedBy;

            public bool HasDoneGlobalSync; // global sync
            public bool HasDoneArenaSync; // arena sync
            public bool HasDoneGlobalCallbacks; // global callbacks

            bool IResettable.TryReset()
            {
                AuthRequest = null;
                ReplacedBy = null;
                HasDoneGlobalSync = false;
                HasDoneArenaSync = false;
                HasDoneGlobalCallbacks = false;
                return true;
            }
        }

        private class AuthRequest : IAuthRequest, IResettable
        {
            private readonly byte[] _loginBytes = new byte[Constants.MaxPacket];
            private int _loginLength = 0;
            private Action<Player>? _doneCallback;
            private readonly AuthResult _result = new();

            public AuthRequest()
            {
                Reset();
            }

            public Player? Player { get; private set; }

            public Span<byte> LoginBytes => _loginBytes.AsSpan(0, _loginLength);

            ReadOnlySpan<byte> IAuthRequest.LoginBytes => new(_loginBytes, 0, _loginLength);

            ref readonly LoginPacket IAuthRequest.LoginPacket
            {
                get
                {
                    if (_loginLength < LoginPacket.VIELength)
                        throw new InvalidOperationException($"The length of {nameof(LoginBytes)} is insufficient.");

                    return ref MemoryMarshal.AsRef<LoginPacket>(LoginBytes);
                }
            }

            ReadOnlySpan<byte> IAuthRequest.ExtraBytes
            {
                get
                {
                    var loginBytes = ((IAuthRequest)this).LoginBytes;
                    return (loginBytes.Length > LoginPacket.VIELength) ? loginBytes[LoginPacket.VIELength..] : ReadOnlySpan<byte>.Empty;
                }
            }

            public AuthResult Result => _result;
            IAuthResult IAuthRequest.Result => _result;

            public void SetRequestInfo(Player player, Span<byte> loginBytes, Action<Player> doneCallback)
            {
                Player = player ?? throw new ArgumentNullException(nameof(player));

                if (loginBytes.Length > _loginBytes.Length)
                    throw new ArgumentException($"Length is greater than {_loginBytes.Length}.", nameof(loginBytes));

                loginBytes.CopyTo(_loginBytes);
                _loginLength = loginBytes.Length;
                _doneCallback = doneCallback ?? throw new ArgumentNullException(nameof(doneCallback));
            }

            public void Done()
            {
                _doneCallback?.Invoke(Player!);
            }

            public void Reset()
            {
                Player = null;
                Array.Clear(_loginBytes);
                _loginLength = 0;
                _doneCallback = null;
                _result.Reset();
            }

            bool IResettable.TryReset()
            {
                Reset();
                return true;
            }
        }

        private class AuthResult : IAuthResult
        {
            public bool DemoData { get; set; }
            public AuthCode? Code { get; set; }
            public bool Authenticated { get; set; }

            #region Name

            private readonly char[] _nameChars = new char[24]; // B2S 0x01 (UserLogin) limits name to 24 bytes
            private int _nameLength;

            public ReadOnlySpan<char> Name => new(_nameChars, 0, _nameLength);

            public void SetName(ReadOnlySpan<char> value)
            {
                if (value.Length > _nameChars.Length)
                    value = value[.._nameChars.Length]; // truncate

                value.CopyTo(_nameChars);
                _nameLength = value.Length;
            }

            #endregion

            #region SendName

            private readonly char[] _sendNameChars = new char[20]; // S2C 0x03 (PlayerEntering) limits name to 20 bytes.
            private int _sendNameLength;

            public ReadOnlySpan<char> SendName => new(_sendNameChars, 0, _sendNameLength);

            public void SetSendName(ReadOnlySpan<char> value)
            {
                if (value.Length > _sendNameChars.Length)
                    value = value[.._sendNameChars.Length]; // truncate

                value.CopyTo(_sendNameChars);
                _sendNameLength = value.Length;
            }

            #endregion

            #region Squad

            private readonly char[] _squadChars = new char[24]; // B2S 0x01 (UserLogin) limits squad to 24 bytes
            private int _squadLength;

            public ReadOnlySpan<char> Squad => new(_squadChars, 0, _squadLength);

            public void SetSquad(ReadOnlySpan<char> value)
            {
                if (value.Length > _squadChars.Length)
                    value = value[.._squadChars.Length]; // truncate

                value.CopyTo(_squadChars);
                _squadLength = value.Length;
            }

            #endregion

            #region CustomText

            private readonly char[] _customTextChars = new char[256];
            private int _customTextLength;

            public ReadOnlySpan<char> CustomText => new(_customTextChars, 0, _customTextLength);

            public int GetMaxCustomTextLength()
            {
                return _customTextChars.Length;
            }

            public void SetCustomText(ReadOnlySpan<char> value)
            {
                if (value.Length > _customTextChars.Length)
                    value = value[.._customTextChars.Length]; // truncate

                value.CopyTo(_customTextChars);
                _customTextLength = value.Length;
            }

            #endregion

            public void Reset()
            {
                DemoData = false;
                Code = null;
                Authenticated = false;
                SetName("");
                SetSendName("");
                SetSquad("");
                SetCustomText("");
            }
        }

        #endregion
    }
}

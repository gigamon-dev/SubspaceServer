using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets;
using SS.Packets.Billing;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for connecting to a billing server via the UDP billing protocol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// To use this module the <see cref="EncryptionVIE"/> module must be loaded first.
    /// </para>
    /// <para>
    /// This module is the equivalent of the 'billing_ssc' module in ASSS.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public class BillingUdp : IModule, IBilling, IAuth, IClientConnectionHandler
    {
        private ComponentBroker _broker;

        // required dependencies
        private ICapabilityManager _capabilityManager;
        private IChat _chat;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMainloopTimer _mainloopTimer;
        private INetwork _network;
        private INetworkClient _networkClient;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;

        // optional dependencies
        private IArenaPlayerStats _arenaPlayerStats;
        private IBillingFallback _billingFallback;

        private InterfaceRegistrationToken<IAuth> _iAuthToken;
        private InterfaceRegistrationToken<IBilling> _iBillingToken;

        private PlayerDataKey<PlayerData> _pdKey;

        private bool _loadPublicPlayerScores;
        private bool _savePublicPlayerScores;
        private TimeSpan _retryTimeSpan;
        private int _pendingAuths;
        private int _interruptedAuths;
        private DateTime? _interruptedAuthsDampTime;
        private BillingState _state;
        private ClientConnection _cc;
        private DateTime _lastEvent;
        private byte[] _identity = null;
        private readonly Dictionary<int, S2B_UserBanner> _bannerUploadDictionary = new();
        private DateTime? _bannerLastSendTime;
        private readonly object _lockObj = new();

        #region Module methods

        [ConfigHelp("Billing", "RetryInterval", ConfigScope.Global, typeof(int), DefaultValue = "180",
            Description = "How many seconds to wait between tries to connect to the user database server.")]
        [ConfigHelp("Billing", "LoadPublicPlayerScores", ConfigScope.Global, typeof(bool), DefaultValue = "0",
            Description = "Whether player scores (for the public arena) should be loaded from the biller. Not recommended, so off by default.")]
        [ConfigHelp("Billing", "SavePublicPlayerScores", ConfigScope.Global, typeof(bool), DefaultValue = "1",
            Description = "Whether player scores (for the public arena) should be saved to the biller.")]
        public bool Load(
            ComponentBroker broker,
            ICapabilityManager capabilityManager,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloopTimer mainloopTimer,
            INetwork network,
            INetworkClient networkClient,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _broker = broker;
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _networkClient = networkClient ?? throw new ArgumentNullException(nameof(networkClient));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _arenaPlayerStats = broker.GetInterface<IArenaPlayerStats>();
            _billingFallback = broker.GetInterface<IBillingFallback>();

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            _network.AddPacket(C2SPacketType.RegData, Packet_RegData);

            _loadPublicPlayerScores = _configManager.GetInt(_configManager.Global, "Billing", "LoadPublicPlayerScores", 0) != 0;
            _savePublicPlayerScores = _configManager.GetInt(_configManager.Global, "Billing", "SavePublicPlayerScores", 1) != 0;
            _retryTimeSpan = TimeSpan.FromSeconds(_configManager.GetInt(_configManager.Global, "Billing", "RetryInterval", 180));

            _mainloopTimer.SetTimer(MainloopTimer_DoWork, 100, 100, null);
            _pendingAuths = _interruptedAuths = 0;

            NewPlayerCallback.Register(broker, Callback_NewPlayer);
            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            ChatMessageCallback.Register(broker, Callback_ChatMessage);
            BannerSetCallback.Register(broker, Callback_BannerSet);

            _commandManager.AddCommand("usage", Command_usage);
            _commandManager.AddCommand("userid", Command_userid);
            _commandManager.AddCommand("userdbadm", Command_userdbadm);
            _commandManager.DefaultCommandReceived += DefaultCommandReceived;

            _iAuthToken = broker.RegisterInterface<IAuth>(this);
            _iBillingToken = broker.RegisterInterface<IBilling>(this);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iBillingToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iAuthToken) != 0)
                return false;

            DropConnection(BillingState.Disabled);

            _commandManager.RemoveCommand("usage", Command_usage);
            _commandManager.RemoveCommand("userid", Command_userid);
            _commandManager.RemoveCommand("userdbadm", Command_userdbadm);
            _commandManager.DefaultCommandReceived -= DefaultCommandReceived;

            NewPlayerCallback.Unregister(broker, Callback_NewPlayer);
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            ChatMessageCallback.Unregister(broker, Callback_ChatMessage);
            BannerSetCallback.Unregister(broker, Callback_BannerSet);

            _mainloopTimer.ClearTimer(MainloopTimer_DoWork, null);

            _network.RemovePacket(C2SPacketType.RegData, Packet_RegData);

            _playerData.FreePlayerData(ref _pdKey);

            broker.ReleaseInterface(ref _arenaPlayerStats);
            broker.ReleaseInterface(ref _billingFallback);

            return true;
        }

        #endregion

        #region IBilling

        BillingStatus IBilling.GetStatus()
        {
            lock (_lockObj)
            {
                return _state switch
                {
                    BillingState.NoSocket => BillingStatus.Down,
                    BillingState.Connecting => BillingStatus.Down,
                    BillingState.WaitLogin => BillingStatus.Down,
                    BillingState.LoggedIn => BillingStatus.Up,
                    BillingState.Retry => BillingStatus.Down,
                    BillingState.LoginFailed => BillingStatus.Disabled,
                    BillingState.Disabled => BillingStatus.Disabled,
                    _ => BillingStatus.Disabled,
                };
            }
        }

        ReadOnlySpan<byte> IBilling.GetIdentity()
        {
            lock (_lockObj)
            {
                return _identity ?? ReadOnlySpan<byte>.Empty;
            }
        }

        bool IBilling.TryGetUserId(Player player, out uint userId)
        {
            if (player is null
                || !player.TryGetExtraData(_pdKey, out PlayerData playerData)
                || !playerData.IsKnownToBiller)
            {
                userId = default;
                return false;
            }

            userId = playerData.BillingUserId;
            return true;
        }

        bool IBilling.TryGetUsage(Player player, out TimeSpan usage, out DateTime? firstLoginTimestamp)
        {
            if (player is null
                || !player.TryGetExtraData(_pdKey, out PlayerData playerData)
                || !playerData.IsKnownToBiller)
            {
                usage = default;
                firstLoginTimestamp = default;
                return false;
            }

            usage = playerData.Usage;
            firstLoginTimestamp = playerData.FirstLogin;
            return true;
        }

        #endregion

        #region IAuth

        void IAuth.Authenticate(IAuthRequest authRequest)
        {
            if (authRequest is null)
                return;

            Player player = authRequest.Player;
            if (player is null
                || !player.TryGetExtraData(_pdKey, out PlayerData playerData)
                || authRequest.LoginBytes.Length < LoginPacket.VIELength)
            {
                authRequest.Result.Code = AuthCode.CustomText;
                authRequest.Result.SetCustomText("Internal server error.");
                return;
            }

            ref readonly LoginPacket loginPacket = ref authRequest.LoginPacket;

            // default to false
            playerData.IsKnownToBiller = false;

            // hold onto the state so that we can use it when authentication is complete
            playerData.AuthRequest = authRequest;

            if (_state == BillingState.LoggedIn)
            {
                if (_pendingAuths < 15 && _interruptedAuths < 20)
                {
                    uint ipAddress = 0;
                    Span<byte> ipBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref ipAddress, 1));
                    if (!player.IPAddress.TryWriteBytes(ipBytes, out int bytesWritten))
                    {
                        FallbackDone(player, BillingFallbackResult.NotFound);
                        return;
                    }

                    ReadOnlySpan<byte> extraBytes = authRequest.ExtraBytes;
                    Span<byte> packetBytes = stackalloc byte[S2B_UserLogin.Length + S2B_UserLogin_ClientExtraData.Length];
                    ref S2B_UserLogin packet = ref MemoryMarshal.AsRef<S2B_UserLogin>(packetBytes);
                    packet = new(
                        loginPacket.Flags,
                        ipAddress,
                        loginPacket.Name,
                        loginPacket.Password,
                        player.Id,
                        loginPacket.MacId,
                        loginPacket.TimeZoneBias,
                        loginPacket.CVersion);

                    if (!extraBytes.IsEmpty)
                    {
                        // There is extra data at the end of the login packet.
                        // For Continuum, it's the ContId field of the login packet.
                        if (extraBytes.Length > S2B_UserLogin_ClientExtraData.Length)
                        {
                            extraBytes = extraBytes[..S2B_UserLogin_ClientExtraData.Length];
                        }

                        ref S2B_UserLogin_ClientExtraData clientExtraData = ref MemoryMarshal.AsRef<S2B_UserLogin_ClientExtraData>(packetBytes[S2B_UserLogin.Length..]);
                        extraBytes.CopyTo(clientExtraData);
                    }

                    _networkClient.SendPacket(_cc, packetBytes[..(S2B_UserLogin.Length + extraBytes.Length)], NetSendFlags.Reliable);
                    playerData.IsKnownToBiller = true;
                    _pendingAuths++;
                }
                else
                {
                    // Tell the user to try again later.                    
                    authRequest.Result.Code = AuthCode.ServerBusy;
                    authRequest.Result.Authenticated = false;
                    authRequest.Done();

                    playerData.AuthRequest = null;
                    _logManager.LogP(LogLevel.Info, nameof(BillingUdp), player, "Too many pending auths, try again later.");
                }
            }
            else if (_billingFallback is not null)
            {
                // Biller isn't connected, use fallback.
                ReadOnlySpan<byte> nameBytes = ((ReadOnlySpan<byte>)loginPacket.Name).SliceNullTerminated();
                Span<char> nameSpan = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(nameBytes)];
                int decodedByteCount = StringUtils.DefaultEncoding.GetChars(nameBytes, nameSpan);
                Debug.Assert(nameBytes.Length == decodedByteCount);

                ReadOnlySpan<byte> passwordBytes = ((ReadOnlySpan<byte>)loginPacket.Password).SliceNullTerminated();
                Span<char> passwordSpan = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(passwordBytes)];
                decodedByteCount = StringUtils.DefaultEncoding.GetChars(passwordBytes, passwordSpan);
                Debug.Assert(passwordBytes.Length == decodedByteCount);

                try
                {
                    _billingFallback.Check(player, nameSpan, passwordSpan, FallbackDone, player);
                }
                finally
                {
                    passwordSpan.Clear();
                }
            }
            else
            {
                // act like not found in fallback
                FallbackDone(player, BillingFallbackResult.NotFound);
            }
        }

        #endregion

        #region IClientConnectionHandler

        [ConfigHelp("Billing", "ServerName", ConfigScope.Global, typeof(string),
            Description = "The server name to send to the user database server.")]
        [ConfigHelp("Billing", "Password", ConfigScope.Global, typeof(string),
            Description = "The password to log in to the user database server with.")]
        [ConfigHelp("Billing", "ServerID", ConfigScope.Global, typeof(int), DefaultValue = "0",
            Description = "ServerID identifying zone to user database server.")]
        [ConfigHelp("Billing", "GroupID", ConfigScope.Global, typeof(int), DefaultValue = "1",
            Description = "GroupID identifying zone to user database server.")]
        [ConfigHelp("Billing", "ScoreID", ConfigScope.Global, typeof(int), DefaultValue = "0",
            Description = "Server realm.")]
        void IClientConnectionHandler.Connected()
        {
            ushort port;
            if (_network.TryGetListenData(0, out IPEndPoint endPoint, out _))
                port = (ushort)endPoint.Port;
            else
                port = 0;

            S2B_ServerConnect packet = new(
                (uint)_configManager.GetInt(_configManager.Global, "Billing", "ServerID", 0),
                (uint)_configManager.GetInt(_configManager.Global, "Billing", "GroupID", 1),
                (uint)_configManager.GetInt(_configManager.Global, "Billing", "ScoreID", 0),
                _configManager.GetStr(_configManager.Global, "Billing", "ServerName"),
                port,
                _configManager.GetStr(_configManager.Global, "Billing", "Password"));

            lock (_lockObj)
            {
                _networkClient.SendPacket(_cc, ref packet, NetSendFlags.Reliable);
                _state = BillingState.WaitLogin;
                _lastEvent = DateTime.UtcNow;
            }
        }

        void IClientConnectionHandler.HandlePacket(Span<byte> data, NetReceiveFlags flags)
        {
            if (data.Length < 1)
                return;

            lock (_lockObj)
            {
                // Move past WaitLogin on any packet.
                if (_state == BillingState.WaitLogin)
                    LoggedIn();

                switch ((B2SPacketType)data[0])
                {
                    case B2SPacketType.UserLogin:
                        ProcessUserLogin(data);
                        break;

                    case B2SPacketType.UserPrivateChat:
                        ProcessUserPrivateChat(data);
                        break;

                    case B2SPacketType.UserKickout:
                        ProcessUserKickout(data);
                        break;

                    case B2SPacketType.UserCommandChat:
                        ProcessUserCommandChat(data);
                        break;

                    case B2SPacketType.UserChannelChat:
                        ProcessUserChannelChat(data);
                        break;

                    case B2SPacketType.ScoreReset:
                        ProcessScoreReset(data);
                        break;

                    case B2SPacketType.UserPacket:
                        ProcessUserPacket(data);
                        break;

                    case B2SPacketType.BillingIdentity:
                        ProcessBillingIdentity(data);
                        break;

                    case B2SPacketType.UserMulticastChannelChat:
                        ProcessUserMulticastChannelChat(data);
                        break;

                    default:
                        _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Unsupported packet type {data[0]}.");
                        break;
                }
            }
        }

        void IClientConnectionHandler.Disconnected()
        {
            _cc = null;
            _logManager.LogM(LogLevel.Info, nameof(BillingUdp), $"Lost connection to user database server (auto-retry in {_retryTimeSpan.TotalSeconds} seconds).");
            DropConnection(BillingState.Retry);
        }

        #endregion

        private void Packet_RegData(Player player, Span<byte> data, NetReceiveFlags flags)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            if (data.Length < 1 || data.Length - 1 > S2B_UserDemographics.DataInlineArray.Length)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(BillingUdp), player, $"Invalid demographics packet (length={data.Length}).");
                return;
            }

            lock (_lockObj)
            {
                if (playerData.HasDemographics)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(BillingUdp), player, "Duplicate demographics packet.");
                    return;
                }

                if (playerData.IsKnownToBiller)
                {
                    Span<byte> packetBytes = stackalloc byte[S2B_UserDemographics.Length];
                    ref S2B_UserDemographics packet = ref MemoryMarshal.AsRef<S2B_UserDemographics>(packetBytes);
                    packet = new(player.Id);
                    int packetLength = packet.SetData(data[1..]);
                    _networkClient.SendPacket(_cc, packetBytes[..packetLength], NetSendFlags.Reliable);

                    playerData.HasDemographics = true;
                }
            }
        }

        [ConfigHelp("Billing", "IP", ConfigScope.Global, typeof(string),
            Description = "The IP address of the user database server (no DNS hostnames allowed).")]
        [ConfigHelp("Billing", "Port", ConfigScope.Global, typeof(int), DefaultValue = "1850",
            Description = "The port to connect to on the user database server.")]
        private bool MainloopTimer_DoWork()
        {
            lock (_lockObj)
            {
                if (_state == BillingState.NoSocket)
                {
                    string ipAddressStr = _configManager.GetStr(_configManager.Global, "Billing", "IP");
                    int port = _configManager.GetInt(_configManager.Global, "Billing", "Port", 1850);

                    if (string.IsNullOrWhiteSpace(ipAddressStr))
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), "No Billing:IP set. User database connectivity disabled.");
                        DropConnection(BillingState.Disabled);
                    }
                    else
                    {
                        // TODO: change to pass in an IPEndPoint or leave it like this?
                        try
                        {
                            _cc = _networkClient.MakeClientConnection(ipAddressStr, port, this, EncryptionVIE.InterfaceIdentifier);
                        }
                        catch (Exception ex)
                        {
                            _cc = null;
                            _state = BillingState.Disabled; // an exception means it was a serious problem and we shouldn't retry
                            _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Unable to make client connection. {ex}");
                        }

                        if (_cc is not null)
                        {
                            _state = BillingState.Connecting;
                            _logManager.LogM(LogLevel.Info, nameof(BillingUdp), $"Connecting to user database server at {ipAddressStr}:{port}.");
                        }
                        else if (_state != BillingState.Disabled)
                        {
                            _state = BillingState.Retry;
                        }

                        _lastEvent = DateTime.UtcNow;
                    }
                }
                else if (_state == BillingState.Connecting)
                {
                    // just wait
                }
                else if (_state == BillingState.WaitLogin)
                {
                    /* this billing protocol doesn't respond to the login packet,
                     * but process_packet will set the next state when it gets any
                     * packet. */
                    /* um, but only ssc proactively sends us a packet after a good
                     * login. for others, assume good after a few seconds without
                     * getting kicked off. */
                    if ((DateTime.UtcNow - _lastEvent).TotalSeconds >= 5)
                    {
                        LoggedIn();
                    }
                }
                else if (_state == BillingState.LoggedIn)
                {
                    DateTime now = DateTime.UtcNow;

                    // Keep alive ping
                    if ((now - _lastEvent).TotalSeconds >= 60)
                    {
                        ReadOnlySpan<byte> packet = [(byte)S2BPacketType.Ping];
                        _networkClient.SendPacket(_cc, packet, NetSendFlags.Reliable);
                        _lastEvent = now;
                    }

                    // Interrupted Auths
                    if (_interruptedAuthsDampTime == null || (now - _interruptedAuthsDampTime.Value).TotalSeconds >= 10)
                    {
                        _interruptedAuths /= 2;
                        _interruptedAuthsDampTime = now;
                    }

                    // Banner upload (limit to sending 1 banner every .2 seconds)
                    if (_bannerUploadDictionary.Count > 1 && (_bannerLastSendTime == null || (now - _bannerLastSendTime.Value).TotalMilliseconds > 200))
                    {
                        // Send one, doesn't matter which.
                        KeyValuePair<int, S2B_UserBanner> kvp = _bannerUploadDictionary.First();
                        _bannerUploadDictionary.Remove(kvp.Key);
                        S2B_UserBanner packet = kvp.Value;
                        _networkClient.SendPacket(_cc, ref packet, NetSendFlags.Reliable);
                        _bannerLastSendTime = now;
                    }
                }
                else if (_state == BillingState.Retry)
                {
                    if (DateTime.UtcNow - _lastEvent > _retryTimeSpan)
                        _state = BillingState.NoSocket;
                }
            }

            return true;
        }

        #region Callbacks

        private void Callback_NewPlayer(Player player, bool isNew)
        {
            if (player is null || isNew)
            {
                return;
            }

            // The player is being removed.
            CleanupPlayer(player);
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            lock (_lockObj)
            {
                if (action == PlayerAction.Disconnect)
                {
                    CleanupPlayer(player);
                }
                else if (action == PlayerAction.EnterArena && arena.IsPublic)
                {
                    if (playerData.LoadedScore != null)
                    {
                        if (_loadPublicPlayerScores && _arenaPlayerStats is not null)
                        {
                            _arenaPlayerStats.SetStat(player, StatCodes.KillPoints, PersistInterval.Reset, playerData.LoadedScore.Value.Points);
                            _arenaPlayerStats.SetStat(player, StatCodes.FlagPoints, PersistInterval.Reset, playerData.LoadedScore.Value.FlagPoints);
                            _arenaPlayerStats.SetStat(player, StatCodes.Kills, PersistInterval.Reset, playerData.LoadedScore.Value.Kills);
                            _arenaPlayerStats.SetStat(player, StatCodes.Deaths, PersistInterval.Reset, playerData.LoadedScore.Value.Deaths);
                            _arenaPlayerStats.SetStat(player, StatCodes.FlagPickups, PersistInterval.Reset, playerData.LoadedScore.Value.Flags);
                        }

                        playerData.LoadedScore = null;
                    }
                }
                else if (action == PlayerAction.LeaveArena && arena.IsPublic && _savePublicPlayerScores && _arenaPlayerStats is not null)
                {
                    if (!_arenaPlayerStats.TryGetStat(player, StatCodes.KillPoints, PersistInterval.Reset, out long killPoints))
                        killPoints = 0;

                    if (!_arenaPlayerStats.TryGetStat(player, StatCodes.FlagPoints, PersistInterval.Reset, out long flagPoints))
                        flagPoints = 0;

                    if (!_arenaPlayerStats.TryGetStat(player, StatCodes.Kills, PersistInterval.Reset, out ulong kills))
                        kills = 0;

                    if (!_arenaPlayerStats.TryGetStat(player, StatCodes.Deaths, PersistInterval.Reset, out ulong deaths))
                        deaths = 0;

                    if (!_arenaPlayerStats.TryGetStat(player, StatCodes.FlagPickups, PersistInterval.Reset, out ulong flagPickups))
                        flagPickups = 0;

                    playerData.SavedScore = new PlayerScore(
                        (ushort)kills,
                        (ushort)deaths,
                        (ushort)flagPickups,
                        (int)killPoints,
                        (int)flagPoints);
                }
            }
        }

        private void CleanupPlayer(Player player)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            lock (_lockObj)
            {
                if (playerData.AuthRequest is not null)
                {
                    // Disconnected while waiting for auth.
                    if (playerData.IsKnownToBiller)
                    {
                        _pendingAuths--;
                        _interruptedAuths++;
                    }

                    playerData.AuthRequest = null;
                }

                _bannerUploadDictionary.Remove(player.Id);

                if (playerData.IsKnownToBiller)
                {
                    S2B_UserLogoff packet = new(
                        player.Id,
                        0, // TODO: put real reason here
                        0, 0, 0, 0); // TODO: get real latency numbers

                    ReadOnlySpan<byte> packetBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1));

                    if (_savePublicPlayerScores && playerData.SavedScore != null)
                    {
                        packet.Score = playerData.SavedScore.Value;
                        _networkClient.SendPacket(_cc, packetBytes, NetSendFlags.Reliable);
                    }
                    else
                    {
                        _networkClient.SendPacket(_cc, packetBytes[..S2B_UserLogoff.LengthWithoutScore], NetSendFlags.Reliable);
                    }

                    playerData.IsKnownToBiller = false;
                }
            }
        }

        private void Callback_ChatMessage(Arena arena, Player player, ChatMessageType type, ChatSound sound, Player toPlayer, short freq, ReadOnlySpan<char> message)
        {
            if (message.Length < 1)
                return;

            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            if (!playerData.IsKnownToBiller)
                return;

            if (type == ChatMessageType.Chat)
            {
                Span<byte> packetBytes = stackalloc byte[S2B_UserChannelChat.MaxLength];
                ref S2B_UserChannelChat packet = ref MemoryMarshal.AsRef<S2B_UserChannelChat>(packetBytes);
                packet = new(player.Id);
                ReadOnlySpan<char> channel = message.GetToken(';', out ReadOnlySpan<char> remaining);

                // Note that this supports a channel name in place of the usual channel number.
                // e.g., ;foo;this is a message to the foo channel
                // Most billers probably don't support this feature yet.
                if (!channel.IsEmpty
                    && StringUtils.DefaultEncoding.GetByteCount(channel) < S2B_UserChannelChat.ChannelInlineArray.Length // < to allow for the null-terminator
                    && remaining.Length > 0) // found ;
                {
                    packet.Channel.Set(channel);
                    message = remaining[1..]; // skip the ;
                }
                else
                {
                    packet.Channel.Set("1");
                }

                int length = S2B_UserChannelChat.SetText(packetBytes, message);

                lock (_lockObj)
                {
                    _networkClient.SendPacket(_cc, packetBytes[..length], NetSendFlags.Reliable);
                }
            }
            else if (type == ChatMessageType.RemotePrivate && toPlayer is null) // remote private message to a player not on the server
            {
                Span<byte> packetBytes = stackalloc byte[S2B_UserPrivateChat.MaxLength];
                ref S2B_UserPrivateChat packet = ref MemoryMarshal.AsRef<S2B_UserPrivateChat>(packetBytes);
                packet = new(
                    -1, // for some odd reason ConnectionID >= 0 indicates global broadcast message
                    1,
                    2,
                    (byte)sound);

                ReadOnlySpan<char> toName = message.GetToken(':', out ReadOnlySpan<char> remaining);
                if (toName.IsEmpty || remaining.Length < 1)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(BillingUdp), player, "Malformed remote private message");
                }
                else
                {
                    StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                    int bytesWritten;

                    try
                    {
                        sb.Append($":{toName}:({player.Name})>{remaining[1..]}");

                        Span<char> textBuffer = stackalloc char[Math.Min(S2B_UserPrivateChat.MaxTextChars, sb.Length)];
                        sb.CopyTo(0, textBuffer, textBuffer.Length);

                        bytesWritten = S2B_UserPrivateChat.SetText(packetBytes, textBuffer);
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                    }

                    lock (_lockObj)
                    {
                        _networkClient.SendPacket(_cc, packetBytes[..bytesWritten], NetSendFlags.Reliable);
                    }
                }
            }
        }

        private void Callback_BannerSet(Player player, ref readonly Banner banner, bool isFromPlayer)
        {
            if (!isFromPlayer)
                return;

            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            lock (_lockObj)
            {
                if (!playerData.IsKnownToBiller)
                    return;

                _bannerUploadDictionary[player.Id] = new S2B_UserBanner(player.Id, in banner);
            }
        }

        #endregion

        #region Command Handlers

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.None,
            Args = null,
            Description = """
                Displays the usage information (current hours and minutes logged in, and
                total hours and minutes logged in), as well as the first login time, of
                the target player, or you if no target.
                """)]
        private void Command_usage(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
                targetPlayer = player;

            if (!((IBilling)this).TryGetUsage(targetPlayer, out TimeSpan usage, out DateTime? firstLoginTimestamp))
            {
                _chat.SendMessage(player, $"Usage unknown for {targetPlayer.Name}.");
                return;
            }

            TimeSpan session = DateTime.UtcNow - targetPlayer.ConnectTime;

            _chat.SendMessage(player, $"Usage: {targetPlayer.Name}");
            _chat.SendMessage(player, $"session: {session}");
            _chat.SendMessage(player, $"  total: {session + usage}");

            if (firstLoginTimestamp is not null)
                _chat.SendMessage(player, $"first played: {firstLoginTimestamp.Value}");
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.None,
            Args = null,
            Description = "Displays the user database id of the target player, or yours if no target.")]
        private void Command_userid(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
                targetPlayer = player;

            if (((IBilling)this).TryGetUserId(targetPlayer, out uint userId))
            {
                _chat.SendMessage(player, $"{targetPlayer.Name} has User ID {userId}");
            }
            else
            {
                _chat.SendMessage(player, $"User ID unknown for {targetPlayer.Name}");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "status|drop|connect|identity",
            Description = """
                The subcommand 'status' reports the status of the user database server
                connection. 'drop' disconnects the connection if it's up. 'connect'
                reconnects after dropping or failed login. 'identity' prints out the
                server's identity if the billing server provided one.
                """)]
        private void Command_userdbadm(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            lock (_lockObj)
            {
                if (parameters.Equals("drop", StringComparison.OrdinalIgnoreCase))
                {
                    DropConnection(BillingState.Disabled);
                    _chat.SendMessage(player, "User database connection disabled.");
                }
                else if (parameters.Equals("connect", StringComparison.OrdinalIgnoreCase))
                {
                    if (_state == BillingState.LoginFailed || _state == BillingState.Disabled || _state == BillingState.Retry)
                    {
                        _state = BillingState.NoSocket;
                        _chat.SendMessage(player, "User database connection reactivated.");
                    }
                    else
                    {
                        _chat.SendMessage(player, "User database server connection already active.");
                    }
                }
                else if (parameters.Equals("identity", StringComparison.OrdinalIgnoreCase))
                {
                    if (_identity is not null && _identity.Length > 0)
                    {
                        _chat.SendMessage(player, $"Identity ({_identity.Length} bytes):");

                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                        try
                        {
                            foreach (byte b in _identity)
                            {
                                if (sb.Length > 0)
                                    sb.Append(' ');

                                sb.Append($"{b:X2}");
                            }

                            _chat.SendWrappedText(player, sb);
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }
                    }
                    else
                    {
                        _chat.SendMessage(player, "Identity not found.");
                    }
                }
                else
                {
                    // Billing status
                    string status = _state switch
                    {
                        BillingState.NoSocket => "not connected yet",
                        BillingState.Connecting => "connecting",
                        BillingState.WaitLogin => "waiting for login response",
                        BillingState.LoggedIn => "logged in",
                        BillingState.Retry => "waiting to retry",
                        BillingState.LoginFailed => "disabled (login failed)",
                        BillingState.Disabled => "disabled (by user)",
                        _ => "unknown",
                    };

                    _chat.SendMessage(player, $"User database status: {status}  Pending auths: {_pendingAuths}");

                    StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        // Connection stats
                        if (_cc is not null)
                        {
                            NetConnectionStats stats = new() { BandwidthLimitInfo = sb };
                            _networkClient.GetConnectionStats(_cc, ref stats);

                            _chat.SendMessage(player, $"IP: {stats.IPEndPoint.Address}  Port: {stats.IPEndPoint.Port}  Encryption: {stats.EncryptorName}");
                            _chat.SendMessage(player, $"Bytes:  Sent: {stats.BytesSent}  Received: {stats.BytesReceived}");
                            _chat.SendMessage(player, $"Packets:  Sent: {stats.PacketsSent}  Received: {stats.PacketsReceived}  Dropped: {stats.PacketsDropped}");
                            _chat.SendMessage(player, $"Reliable Packets: Sent: {stats.ReliablePacketsSent}  Received: {stats.ReliablePacketsReceived}");
                            _chat.SendMessage(player, $"Reliable: Dups: {stats.RelDups} {stats.RelDups * 100d / stats.ReliablePacketsReceived:F2}%  Resends: {stats.Retries} {stats.Retries * 100d / stats.ReliablePacketsSent:F2}%");
                            _chat.SendMessage(player, $"Reliable S2B Packetloss: {((stats.Retries == 0) ? 0d : ((stats.Retries - stats.AckDups) * 100d / stats.ReliablePacketsSent)):F2}%");
                            _chat.SendMessage(player, $"BW limit: {stats.BandwidthLimitInfo}");
                        }
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                    }
                }
            }
        }

        private void DefaultCommandReceived(ReadOnlySpan<char> commandName, ReadOnlySpan<char> line, Player player, ITarget target)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            if (!playerData.IsKnownToBiller)
                return;

            if (target.Type != TargetType.Arena)
            {
                _logManager.LogP(LogLevel.Drivel, nameof(BillingUdp), player, $"Unknown command with bad target: '{line}'.");
                return;
            }

            if (_chat.GetPlayerChatMask(player).IsRestricted(ChatMessageType.BillerCommand))
                return;

            Span<byte> packetBytes = stackalloc byte[S2B_UserCommand.MaxLength];
            ref S2B_UserCommand packet = ref MemoryMarshal.AsRef<S2B_UserCommand>(packetBytes);
            packet = new(player.Id);
            int length;

            if (line.StartsWith("chat=", StringComparison.OrdinalIgnoreCase) || line.StartsWith("chat ", StringComparison.OrdinalIgnoreCase))
            {
                length = RewriteChatCommand(player, line, packetBytes);
            }
            else
            {
                // Write the command prepended with the question mark.
                length = S2B_UserCommand.SetText(packetBytes, line, true);
            }

            lock (_lockObj)
            {
                _networkClient.SendPacket(_cc, packetBytes[..length], NetSendFlags.Reliable);
            }

            [ConfigHelp("Billing", "StaffChats", ConfigScope.Global, typeof(string), Description = "Comma separated staff chat list.")]
            [ConfigHelp("Billing", "StaffChatPrefix", ConfigScope.Global, typeof(string), Description = "Secret prefix to prepend to staff chats.")]
            [ConfigHelp("Billing", "LocalChats", ConfigScope.Global, typeof(string), Description = "Comma separated local chat list.")]
            [ConfigHelp("Billing", "LocalChatPrefix", ConfigScope.Global, typeof(string), Description = "Secret prefix to prepend to local chats.")]
            int RewriteChatCommand(Player player, ReadOnlySpan<char> line, Span<byte> packetBytes)
            {
                static bool FindChat(ReadOnlySpan<char> searchFor, ReadOnlySpan<char> list)
                {
                    if (searchFor.IsEmpty || list.IsEmpty)
                        return false;

                    ReadOnlySpan<char> chatName;
                    while ((chatName = list.GetToken(',', out list)).Length > 0)
                    {
                        if (searchFor.Equals(chatName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }

                    return false;
                }

                ReadOnlySpan<char> staffChats = _configManager.GetStr(_configManager.Global, "Billing", "StaffChats");
                ReadOnlySpan<char> staffPrefix = _configManager.GetStr(_configManager.Global, "Billing", "StaffChatPrefix");
                ReadOnlySpan<char> localChats = _configManager.GetStr(_configManager.Global, "Billing", "LocalChats");
                ReadOnlySpan<char> localPrefix = _configManager.GetStr(_configManager.Global, "Billing", "LocalChatPrefix");

                line = line[5..]; // skip "chat=" or "chat " (there is no ? at the beginning, it was already removed)

                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                try
                {
                    sb.Append("?chat=");

                    ReadOnlySpan<char> chatName;
                    while ((chatName = line.GetToken(',', out line)).Length > 0)
                    {
                        chatName = chatName.Trim();
                        if (chatName.Length <= 0 || chatName.Length > 31)
                            continue;

                        bool addComma = sb.Length > 6;

                        if (!localChats.IsWhiteSpace()
                            && FindChat(chatName, localChats))
                        {
                            if ((localPrefix.Length + 4 + chatName.Length) > 31
                                || (localPrefix.Length + 4 + chatName.Length + sb.Length + (addComma ? 1 : 0)) > S2B_UserCommand.MaxTextChars)
                            {
                                continue;
                            }

                            if (addComma)
                                sb.Append(',');

                            sb.Append("$l$");
                            sb.Append(localPrefix);
                            sb.Append('|');
                            sb.Append(chatName);
                        }
                        else if (!staffChats.IsWhiteSpace()
                            && FindChat(chatName, staffChats)
                            && _capabilityManager.HasCapability(player, Constants.Capabilities.SendModChat))
                        {
                            if ((staffPrefix.Length + 4 + chatName.Length) > 31
                                || (staffPrefix.Length + 4 + chatName.Length + sb.Length + (addComma ? 1 : 0)) > S2B_UserCommand.MaxTextChars)
                            {
                                continue;
                            }

                            if (addComma)
                                sb.Append(',');

                            sb.Append("$s$");
                            sb.Append(staffPrefix);
                            sb.Append('|');
                            sb.Append(chatName);
                        }
                        else
                        {
                            if (chatName.Length > 31
                                || chatName.Length + sb.Length + (addComma ? 1 : 0) > S2B_UserCommand.MaxTextChars)
                            {
                                continue;
                            }

                            if (addComma)
                                sb.Append(',');

                            sb.Append(chatName);
                        }
                    }

                    Span<char> textBuffer = stackalloc char[Math.Min(S2B_UserCommand.MaxTextChars, sb.Length)];
                    sb.CopyTo(0, textBuffer, textBuffer.Length);
                    return S2B_UserCommand.SetText(packetBytes, textBuffer, false);
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }
        }

        #endregion

        private void FallbackDone(Player player, BillingFallbackResult fallbackResult)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            if (playerData.AuthRequest is null)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), "Unexpected billing fallback response.");
                return;
            }

            IAuthResult result = playerData.AuthRequest.Result;

            if (fallbackResult == BillingFallbackResult.Match)
            {
                // Correct password, player is ok and authenticated.
                result.Code = AuthCode.OK;
                result.Authenticated = true;

                ReadOnlySpan<byte> nameBytes = ((ReadOnlySpan<byte>)playerData.AuthRequest.LoginPacket.Name).SliceNullTerminated();
                Span<char> nameChars = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(nameBytes)];
                int decodedByteCount = StringUtils.DefaultEncoding.GetChars(nameBytes, nameChars);
                Debug.Assert(nameBytes.Length == decodedByteCount);
                result.SetName(nameChars);
                result.SetSendName(nameChars);

                result.SetSquad("");
                _logManager.LogP(LogLevel.Info, nameof(BillingUdp), player, "Fallback: authenticated.");
            }
            else if (fallbackResult == BillingFallbackResult.NotFound)
            {
                // Add ^ in front of name and accept as unathenticated.
                result.Code = AuthCode.OK;
                result.Authenticated = false;

                ReadOnlySpan<byte> nameBytes = ((ReadOnlySpan<byte>)playerData.AuthRequest.LoginPacket.Name).SliceNullTerminated();
                Span<char> nameChars = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(nameBytes) + 1];
                nameChars[0] = '^';
                int decodedByteCount = StringUtils.DefaultEncoding.GetChars(nameBytes, nameChars[1..]);
                Debug.Assert(nameBytes.Length == decodedByteCount);
                result.SetName(nameChars);
                result.SetSendName(nameChars);

                result.SetSquad("");
                playerData.IsKnownToBiller = false;
                _logManager.LogP(LogLevel.Info, nameof(BillingUdp), player, "Fallback: no entry for this player.");
            }
            else // Mismatch or anything else
            {
                result.Code = AuthCode.BadPassword;
                result.Authenticated = false;
                _logManager.LogP(LogLevel.Info, nameof(BillingUdp), player, "Fallback: invalid password.");
            }

            playerData.AuthRequest.Done();
            playerData.AuthRequest = null;
        }

        private void LoggedIn()
        {
            _chat.SendArenaMessage((Arena)null, "Notice: Connection to user database server restored. Log in again for full functionality.");

            S2B_ServerCapabilities packet = new(multiCastChat: true, supportDemographics: true);
            _networkClient.SendPacket(_cc, ref packet, NetSendFlags.Reliable);
            _state = BillingState.LoggedIn;
            _identity = null;

            _logManager.LogM(LogLevel.Info, nameof(BillingUdp), "Logged in to user database server.");
        }

        private void DropConnection(BillingState newState)
        {
            // Only announce if changing from LoggedIn
            if (_state == BillingState.LoggedIn)
            {
                _chat.SendArenaMessage((Arena)null, "Notice: Connection to user database server lost.");
            }

            // Clear KnownToBiller values
            _playerData.Lock();

            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (player.TryGetExtraData(_pdKey, out PlayerData playerData))
                    {
                        playerData.IsKnownToBiller = false;
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            // Close the client connection
            if (_cc is not null)
            {
                // Ideally this would be sent reliably, but reliable packets won't get sent after DropConnection.
                ReadOnlySpan<byte> disconnect = [(byte)S2BPacketType.ServerDisconnect];
                _networkClient.SendPacket(_cc, disconnect, NetSendFlags.PriorityP5);
                _networkClient.DropConnection(_cc);
                _cc = null;
            }

            _state = newState;
            _lastEvent = DateTime.UtcNow;
        }

        #region B2S packet handlers

        private void ProcessUserLogin(Span<byte> data)
        {
            if (data.Length < B2S_UserLogin.LengthWithoutScore)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserLogin)} packet length ({data.Length}).");
                return;
            }

            ref B2S_UserLogin packet = ref MemoryMarshal.AsRef<B2S_UserLogin>(data);

            Player player = _playerData.PidToPlayer(packet.ConnectionId);
            if (player is null)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Biller sent {nameof(B2S_UserLogin)} for unknown pid {packet.ConnectionId}.");
                return;
            }

            if (!player.TryGetExtraData(_pdKey, out PlayerData playerData))
                return;

            if (playerData.AuthRequest is null)
            {
                _logManager.LogP(LogLevel.Warn, nameof(BillingUdp), player, $"Unexpected {nameof(B2S_UserLogin)} response.");
                return;
            }

            IAuthResult result = playerData.AuthRequest.Result;

            if (packet.Result == B2SUserLoginResult.Ok
                || packet.Result == B2SUserLoginResult.DemoVersion
                || packet.Result == B2SUserLoginResult.AskDemographics)
            {
                playerData.FirstLogin = packet.FirstLogin.ToDateTime();
                playerData.Usage = packet.Usage;
                playerData.BillingUserId = packet.UserId;

                // Note: ASSS has this commented out, but here we provide a config setting.
                if (_loadPublicPlayerScores && data.Length >= B2S_UserLogin.LengthWithScore)
                {
                    ref PlayerScore score = ref MemoryMarshal.AsRef<PlayerScore>(data[B2S_UserLogin.LengthWithoutScore..]);
                    playerData.LoadedScore = score;
                }
                else
                {
                    playerData.LoadedScore = null;
                }

                result.DemoData = packet.Result == B2SUserLoginResult.AskDemographics || packet.Result == B2SUserLoginResult.DemoVersion;
                result.Code = result.DemoData ? AuthCode.AskDemographics : AuthCode.OK;
                result.Authenticated = true;

                ReadOnlySpan<byte> nameBytes = ((ReadOnlySpan<byte>)packet.Name).SliceNullTerminated();
                Span<char> nameChars = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(nameBytes)];
                int decodedByteCount = StringUtils.DefaultEncoding.GetChars(nameBytes, nameChars);
                Debug.Assert(nameBytes.Length == decodedByteCount);
                result.SetName(nameChars);
                result.SetSendName(nameChars);

                ReadOnlySpan<byte> squadBytes = ((ReadOnlySpan<byte>)packet.Squad).SliceNullTerminated();
                Span<char> squadChars = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(squadBytes)];
                decodedByteCount = StringUtils.DefaultEncoding.GetChars(squadBytes, squadChars);
                Debug.Assert(squadBytes.Length == decodedByteCount);
                result.SetSquad(squadChars);

                if (packet.Banner.IsSet)
                {
                    IBanners banners = _broker.GetInterface<IBanners>();
                    if (banners is not null)
                    {
                        try
                        {
                            banners.SetBanner(player, in packet.Banner);
                        }
                        finally
                        {
                            _broker.ReleaseInterface(ref banners);
                        }
                    }
                }

                _logManager.LogP(LogLevel.Info, nameof(BillingUdp), player, $"Player authenticated {packet.Result} with user id {packet.UserId}.");
            }
            else
            {
                result.DemoData = false;

                result.Code = packet.Result switch
                {
                    B2SUserLoginResult.NewUser => AuthCode.NewName,
                    B2SUserLoginResult.InvalidPw => AuthCode.BadPassword,
                    B2SUserLoginResult.Banned => AuthCode.LockedOut,
                    B2SUserLoginResult.NoNewConns => AuthCode.NoNewConn,
                    B2SUserLoginResult.BadUserName => AuthCode.BadName,
                    B2SUserLoginResult.ServerBusy => AuthCode.ServerBusy,
                    _ => AuthCode.NoPermission,
                };

                result.Authenticated = false;

                _logManager.LogP(LogLevel.Info, nameof(BillingUdp), player, $"Player rejected ({packet.Result} / {result.Code}).");
            }

            playerData.AuthRequest.Done();
            playerData.AuthRequest = null;
            _pendingAuths--;
        }

        private void ProcessUserPrivateChat(Span<byte> data)
        {
            if (data.Length < B2S_UserPrivateChat.MinLength || data.Length > B2S_UserPrivateChat.MaxLength)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserPrivateChat)} - length ({data.Length}).");
                return;
            }

            ref B2S_UserPrivateChat packet = ref MemoryMarshal.AsRef<B2S_UserPrivateChat>(data);

            if (packet.SubType != 2)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserPrivateChat)} - SubType ({packet.SubType}).");
                return;
            }

            Span<byte> textBytes = B2S_UserPrivateChat.GetTextBytes(data);
            int index = textBytes.IndexOf((byte)0);
            if (index == -1)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserPrivateChat)} - Text not null-terminated.");
                return;
            }

            textBytes = textBytes[..index];
            Span<char> text = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(textBytes)];
            StringUtils.DefaultEncoding.GetChars(textBytes, text);

            if (text.Length >= 1 && text[0] == ':') // private message
            {
                ProcessRemotePrivateMessage((ChatSound)packet.Sound, text);
            }
            else // broadcast message
            {
                _chat.SendArenaMessage((Arena)null, (ChatSound)packet.Sound, text);
            }


            void ProcessRemotePrivateMessage(ChatSound sound, ReadOnlySpan<char> text)
            {
                // Remote private messages should be in the format:
                // :recipient:(sender)>message
                // recipient can be a squad, in which case it begins with a #
                // if sender has )> in their name, then too bad, we take the first )>

                Span<Range> ranges = stackalloc Range[3];
                int numRanges = text.Split(ranges, ':', StringSplitOptions.None);
                if (numRanges != 3 || !text[ranges[0]].IsEmpty)
                    return;

                ReadOnlySpan<char> recipient = text[ranges[1]];
                if (recipient.IsEmpty)
                    return;

                bool isSquadRecipient = recipient[0] == '#';
                if (isSquadRecipient)
                {
                    recipient = recipient[1..];

                    if (recipient.IsEmpty || recipient.Length > Constants.MaxSquadNameLength)
                        return;
                }
                else if (recipient.Length > Constants.MaxPlayerNameLength)
                {
                    return;
                }

                ReadOnlySpan<char> remaining = text[ranges[2]];
                if (remaining.IsEmpty || remaining[0] != '(')
                    return;

                remaining = remaining[1..];
                numRanges = remaining.Split(ranges[..2], ")>", StringSplitOptions.None);
                if (numRanges != 2)
                    return;

                ReadOnlySpan<char> sender = remaining[ranges[0]];
                if (sender.IsEmpty)
                    return;

                ReadOnlySpan<char> message = remaining[ranges[1]];

                if (isSquadRecipient)
                {
                    // squad message
                    HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                    try
                    {
                        _playerData.Lock();

                        try
                        {
                            foreach (Player player in _playerData.Players)
                                if (MemoryExtensions.Equals(player.Squad, recipient, StringComparison.OrdinalIgnoreCase))
                                    set.Add(player);
                        }
                        finally
                        {
                            _playerData.Unlock();
                        }

                        if (set.Count == 0)
                            return;

                        _chat.SendRemotePrivMessage(set, sound, recipient, sender, message);
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(set);
                    }
                }
                else
                {
                    Player player = _playerData.FindPlayer(recipient);
                    if (player is null)
                        return;

                    HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                    try
                    {
                        set.Add(player);
                        _chat.SendRemotePrivMessage(set, sound, null, sender, message);
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(set);
                    }
                }
            }
        }

        private void ProcessUserKickout(Span<byte> data)
        {
            if (data.Length < B2S_UserKickout.Length)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserKickout)} packet length {data.Length}.");
                return;
            }

            ref B2S_UserKickout packet = ref MemoryMarshal.AsRef<B2S_UserKickout>(data);

            Player player = _playerData.PidToPlayer(packet.ConnectionId);
            if (player is not null)
            {
                _playerData.KickPlayer(player);
                _logManager.LogP(LogLevel.Info, nameof(BillingUdp), player, $"Player kicked out by user database server ({packet.Reason}).");
            }
        }

        private void ProcessUserCommandChat(Span<byte> data)
        {
            if (data.Length < B2S_UserCommandChat.MinLength || data.Length > B2S_UserCommandChat.MaxLength)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserCommandChat)} - length ({data.Length}).");
                return;
            }

            ref B2S_UserCommandChat packet = ref MemoryMarshal.AsRef<B2S_UserCommandChat>(data);

            Player player = _playerData.PidToPlayer(packet.ConnectionId);
            if (player is null)
            {
                _logManager.LogM(LogLevel.Info, nameof(BillingUdp), $"Invalid {nameof(B2S_UserCommandChat)} - player not found ({packet.ConnectionId}).");
                return;
            }

            Span<byte> textBytes = B2S_UserCommandChat.GetTextBytes(data);
            int index = textBytes.IndexOf((byte)0);
            if (index == -1)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserCommandChat)} - Text not null-terminated.");
                return;
            }

            textBytes = textBytes[..index];
            Span<char> text = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(textBytes)];
            StringUtils.DefaultEncoding.GetChars(textBytes, text);

            index = text.IndexOf('|'); // local and staff chats have a pipe appended
            if (index != -1 && text.Length > 3 && MemoryExtensions.Equals(text[..3], "$l$", StringComparison.Ordinal))
            {
                _chat.SendMessage(player, $"(local) {text[(index + 1)..]}");
            }
            else if (index != -1 && text.Length > 3 && MemoryExtensions.Equals(text[..3], "$s$", StringComparison.Ordinal))
            {
                _chat.SendMessage(player, $"(staff) {text[(index + 1)..]}");
            }
            else
            {
                _chat.SendMessage(player, text);
            }
        }

        private void ProcessUserChannelChat(Span<byte> data)
        {
            if (data.Length < B2S_UserChannelChat.MinLength || data.Length > B2S_UserChannelChat.MaxLength)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserChannelChat)} - length ({data.Length}).");
                return;
            }

            ref B2S_UserChannelChat packet = ref MemoryMarshal.AsRef<B2S_UserChannelChat>(data);

            Player player = _playerData.PidToPlayer(packet.ConnectionId);
            if (player is null)
            {
                _logManager.LogM(LogLevel.Info, nameof(BillingUdp), $"Invalid {nameof(B2S_UserChannelChat)} - player not found ({packet.ConnectionId}).");
                return;
            }

            Span<byte> textBytes = B2S_UserChannelChat.GetTextBytes(data);
            int index = textBytes.IndexOf((byte)0);
            if (index == -1)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserChannelChat)} - Text not null-terminated.");
                return;
            }

            textBytes = textBytes[..index];

            int numChars = StringUtils.DefaultEncoding.GetCharCount(textBytes);
            Span<char> messageBuffer = stackalloc char[3 + 1 + numChars]; // enough for channel + ':' + text
            if (!packet.Channel.TryFormat(messageBuffer, out int charsWritten))
                return;

            messageBuffer[charsWritten++] = ':';
            Span<char> textBuffer = messageBuffer[charsWritten..];
            StringUtils.DefaultEncoding.GetChars(textBytes, textBuffer);
            messageBuffer = messageBuffer[..(charsWritten + numChars)];

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                set.Add(player);
                _chat.SendAnyMessage(set, ChatMessageType.Chat, ChatSound.None, null, messageBuffer);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        [ConfigHelp("Billing", "HonorScoreResetRequests", ConfigScope.Global, typeof(bool), DefaultValue = "1",
            Description = "Whether to reset scores when the billing server says it is time to.")]
        [ConfigHelp("Billing", "ScoreResetArenaGroups", ConfigScope.Global, typeof(string), DefaultValue = Constants.ArenaGroup_Public,
            Description = "Which arena group(s) to affect when honoring a billing server score reset request.")]
        private void ProcessScoreReset(Span<byte> data)
        {
            if (data.Length < B2S_ScoreReset.Length)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_ScoreReset)} - length ({data.Length}).");
                return;
            }

            ref B2S_ScoreReset packet = ref MemoryMarshal.AsRef<B2S_ScoreReset>(data);
            if (packet.ScoreId != -packet.ScoreIdNegative)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_ScoreReset)} - ScoreId mismatch ({packet.ScoreId}/{packet.ScoreIdNegative}).");
                return;
            }

            if (_configManager.GetInt(_configManager.Global, "Billing", "HonorScoreResetRequests", 1) != 0)
            {
                IPersistExecutor persistExecutor = _broker.GetInterface<IPersistExecutor>();

                if (persistExecutor is not null)
                {
                    try
                    {
                        string arenaGroups = _configManager.GetStr(_configManager.Global, "Billing", "ScoreResetArenaGroups");
                        arenaGroups ??= Constants.ArenaGroup_Public; // null only, white-space means none

                        ReadOnlySpan<char> remaining = arenaGroups;
                        ReadOnlySpan<char> token;
                        while ((token = remaining.GetToken(", \t", out remaining)).Length > 0)
                        {
                            persistExecutor.EndInterval(PersistInterval.Reset, token.ToString());
                            _logManager.LogM(LogLevel.Info, nameof(BillingUdp), $"Billing server requested a score reset, resetting scores of arena group '{token}'.");
                        }
                    }
                    finally
                    {
                        _broker.ReleaseInterface(ref persistExecutor);
                    }
                }
                else
                {
                    _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), "Billing server requested score reset, but the IPersistExecutor interface is not available.");
                }
            }
            else
            {
                _logManager.LogM(LogLevel.Info, nameof(BillingUdp), "Billing server requested score reset, but honoring such requests is disabled (Billing:HonorScoreResetRequests).");
            }
        }

        private void ProcessUserPacket(Span<byte> data)
        {
            if (data.Length < B2S_UserPacketHeader.Length)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid B2S UserPacket - length ({data.Length}).");
                return;
            }

            ref readonly B2S_UserPacketHeader header = ref MemoryMarshal.AsRef<B2S_UserPacketHeader>(data[..B2S_UserPacketHeader.Length]);

            ReadOnlySpan<byte> dataBytes = data[B2S_UserPacketHeader.Length..];
            if (dataBytes.IsEmpty)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid B2S UserPacket - length ({data.Length}).");
                return;
            }

            if (header.ConnectionId == -1)
            {
                // Send to all players not allowed.
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"B2S UserPacket filtered (target all).");
                return;
            }

            Player player = _playerData.PidToPlayer(header.ConnectionId);
            if (player is null)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"B2S UserPacket unknown pid ({header.ConnectionId}).");
                return;
            }

            // Only allow S2CPacketType.LoginText for banned players to get the ban text.
            if (dataBytes[0] == (byte)S2CPacketType.LoginText)
            {
                _network.SendToOne(player, dataBytes, NetSendFlags.Reliable);
            }
            else
            {
                _logManager.LogP(LogLevel.Warn, nameof(BillingUdp), player, $"B2S UserPacket filtered (type: 0x{dataBytes[0]:X2}).");
            }
        }

        private void ProcessBillingIdentity(Span<byte> data)
        {
            if (data.Length < 1)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid B2S BillingIdentity - length ({data.Length}).");
                return;
            }

            data = data[1..];
            _identity = data.IsEmpty ? null : data.ToArray();
        }

        private void ProcessUserMulticastChannelChat(Span<byte> data)
        {
            int length = data.Length;
            if (length < B2S_UserMulticastChannelChatHeader.Length)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid B2S UserMulticastChannelChat - length ({length}).");
                return;
            }

            // Header
            ref readonly B2S_UserMulticastChannelChatHeader header = ref MemoryMarshal.AsRef<B2S_UserMulticastChannelChatHeader>(data[..B2S_UserMulticastChannelChatHeader.Length]);
            if (header.Count < 1)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid B2S UserMulticastChannelChat - {header.Count} recipients.");
                return;
            }

            data = data[B2S_UserMulticastChannelChatHeader.Length..];

            // Recipients
            int recipientsLength = header.Count * MulticastChannelChatRecipient.Length;
            if (data.Length < recipientsLength + 1) // +1 for at least one byte of text
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid B2S UserMulticastChannelChat - length ({length}) for {header.Count} recipients.");
                return;
            }

            ReadOnlySpan<MulticastChannelChatRecipient> recipients = MemoryMarshal.Cast<byte, MulticastChannelChatRecipient>(data[..recipientsLength]);
            Debug.Assert(header.Count == recipients.Length);

            data = data[recipientsLength..];

            // Text
            if (data.Length > ChatPacket.MaxMessageBytes)
            {
                data = data[..ChatPacket.MaxMessageBytes];
            }

            int index = data.IndexOf((byte)0);
            if (index == -1)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid B2S UserMulticastChannelChat - Text not null-terminated.");
                return;
            }

            data = data[..index];
            if (data.IsEmpty)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid B2S UserMulticastChannelChat - Text was empty.");
                return;
            }

            Span<char> text = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(data)];
            int numDecodedBytes = StringUtils.DefaultEncoding.GetChars(data, text);
            Debug.Assert(data.Length == numDecodedBytes);

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                Span<char> messageBuffer = stackalloc char[ChatPacket.MaxMessageChars];
                for (int i = 0; i < recipients.Length; i++)
                {
                    ref readonly MulticastChannelChatRecipient recipient = ref recipients[i];

                    Player player = _playerData.PidToPlayer(recipient.ConnectionId);
                    if (player is null)
                        continue;

                    set.Clear();
                    set.Add(player);

                    sb.Clear();
                    sb.Append(recipient.Channel);
                    sb.Append(':');
                    sb.Append(text);

                    int numCharacters = sb.Length;
                    if (numCharacters > messageBuffer.Length)
                        numCharacters = messageBuffer.Length;

                    sb.CopyTo(0, messageBuffer, numCharacters);

                    _chat.SendAnyMessage(set, ChatMessageType.Chat, ChatSound.None, null, messageBuffer[..numCharacters]);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        #endregion

        private class PlayerData : IResettable
        {
            public uint BillingUserId;
            public TimeSpan Usage;
            public bool IsKnownToBiller;
            public bool HasDemographics;
            public IAuthRequest AuthRequest;
            public DateTime? FirstLogin;
            public PlayerScore? LoadedScore;
            public PlayerScore? SavedScore;

            bool IResettable.TryReset()
            {
                BillingUserId = 0;
                Usage = TimeSpan.Zero;
                IsKnownToBiller = false;
                HasDemographics = false;
                AuthRequest = null;
                FirstLogin = null;
                LoadedScore = null;
                SavedScore = null;
                return true;
            }
        }

        private enum BillingState
        {
            NoSocket,
            Connecting,
            WaitLogin,
            LoggedIn,
            Retry,
            LoginFailed,
            Disabled,
        }
    }
}

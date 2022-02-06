using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets;
using SS.Packets.Game;
using SS.Packets.Billing;
using SS.Utilities;
using System;
using System.Collections.Generic;
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
    /// This module is the equivalent of the 'billing_ssc' module in ASSS.
    /// </remarks>
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
        // TODO: private IStats _stats;

        // optional dependencies
        private IBillingFallback _billingFallback;

        private InterfaceRegistrationToken _iAuthToken;
        private InterfaceRegistrationToken _iBillingToken;

        private int _pdKey;

        private TimeSpan _retryTimeSpan;
        private int _pendingAuths;
        private int _interruptedAuths;
        private DateTime? _interruptedAuthsDampTime;
        private BillingState _state;
        private ClientConnection _cc;
        private DateTime _lastEvent;
        private byte[] _identity = new byte[256];
        private readonly Dictionary<int, S2B_UserBanner> _bannerUploadDictionary = new();
        private DateTime? _bannerLastSendTime;
        private readonly object _lockObj = new();

        #region Module methods

        [ConfigHelp("Billing", "RetryInterval", ConfigScope.Global, typeof(int), DefaultValue = "180",
            Description = "How many seconds to wait between tries to connect to the user database server.")]
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
            IPlayerData playerData
            // TODO: IStats stats
            )
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

            _billingFallback = broker.GetInterface<IBillingFallback>();

            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            _network.AddPacket(C2SPacketType.RegData, Packet_RegData);

            _retryTimeSpan = TimeSpan.FromSeconds(_configManager.GetInt(_configManager.Global, "Billing", "RetryInterval", 180));

            _mainloopTimer.SetTimer(MainloopTimer_DoWork, 100, 100, null);
            _pendingAuths = _interruptedAuths = 0;

            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            ChatMessageCallback.Register(broker, Callback_ChatMessage);
            SetBannerCallback.Register(broker, Callback_SetBanner);

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
            if (broker.UnregisterInterface<IBilling>(ref _iBillingToken) != 0)
                return false;

            if (broker.UnregisterInterface<IAuth>(ref _iAuthToken) != 0)
                return false;

            DropConnection(BillingState.Disabled);

            _commandManager.RemoveCommand("usage", Command_usage);
            _commandManager.RemoveCommand("userid", Command_userid);
            _commandManager.RemoveCommand("userdbadm", Command_userdbadm);
            _commandManager.DefaultCommandReceived -= DefaultCommandReceived;

            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            ChatMessageCallback.Unregister(broker, Callback_ChatMessage);
            SetBannerCallback.Unregister(broker, Callback_SetBanner);

            _mainloopTimer.ClearTimer(MainloopTimer_DoWork, null);

            _network.RemovePacket(C2SPacketType.RegData, Packet_RegData);

            _playerData.FreePlayerData(_pdKey);

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

        #endregion

        #region IAuth

        void IAuth.Authenticate(Player p, in LoginPacket lp, int lplen, AuthDoneDelegate done)
        {
            if (lplen < 0)
            {
                FallbackDone(p, BillingFallbackResult.NotFound);
                return;
            }

            if (p[_pdKey] is not PlayerData pd)
            {
                FallbackDone(p, BillingFallbackResult.NotFound);
                return;
            }

            // default to false
            pd.IsKnownToBiller = false;

            // set up temporary login data struct
            pd.LoginData = new LoginData()
            {
                DoneCallback = done,
                Name = lp.Name,
            };

            if (_state == BillingState.LoggedIn)
            {
                if (_pendingAuths < 15 && _interruptedAuths < 20)
                {
                    uint ipAddress = 0;
                    Span<byte> ipBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref ipAddress, 1));
                    if (!p.IpAddress.TryWriteBytes(ipBytes, out int bytesWritten))
                    {
                        FallbackDone(p, BillingFallbackResult.NotFound);
                        return;
                    }

                    S2B_UserLogin packet = new(
                        lp.Flags,
                        ipAddress,
                        lp.NameBytes,
                        lp.PasswordBytes,
                        p.Id,
                        lp.MacId,
                        lp.TimeZoneBias,
                        lp.CVersion);

                    int packetLength = S2B_UserLogin.LengthWithoutClientExtraData;

                    if (lplen > LoginPacket.VIELength)
                    {
                        // There is extra data at the end of the login packet.
                        // For Continuum, it's the ContId field of the login packet.
                        int extraLength = lplen - LoginPacket.VIELength;
                        if (extraLength > lp.ContId.Length)
                            extraLength = lp.ContId.Length;

                        // TODO: ASSS allows sending even more extra data bytes if the CFG_RELAX_LENGTH_CHECKS is defined.
                        // To support that, LoginPacket would need to be changed or the IAuth.Auth method would need to pass a byte[] or Span<byte> instead.
                        //if (extraLength > S2B_UserLogin.ClientExtraDataBytesLength)
                        //    extraLength = S2B_UserLogin.ClientExtraDataBytesLength;

                        lp.ContId.Slice(0, extraLength).CopyTo(packet.ClientExtraDataBytes);
                        packetLength += extraLength;
                    }

                    ReadOnlySpan<byte> packetBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1)).Slice(0, packetLength);
                    _networkClient.SendPacket(_cc, packetBytes, NetSendFlags.Reliable);
                    pd.IsKnownToBiller = true;
                    _pendingAuths++;
                }
                else
                {
                    // Tell the user to try again later.
                    AuthData authData = new();
                    authData.Code = AuthCode.ServerBusy;
                    authData.Authenticated = false;
                    done(p, authData);

                    pd.RemoveLoginData();
                    _logManager.LogP(LogLevel.Info, nameof(BillingUdp), p, "Too many pending auths, try again later.");
                }
            }
            else if (_billingFallback != null)
            {
                // biller isn't connected, use fallback
                Span<byte> passwordBytes = lp.PasswordBytes.SliceNullTerminated();
                Span<char> passwordSpan = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(passwordBytes)];
                StringUtils.DefaultEncoding.GetChars(passwordBytes, passwordSpan);

                try
                {
                    _billingFallback.Check(p, pd.LoginData.Value.Name, passwordSpan, FallbackDone, p);
                }
                finally
                {
                    passwordSpan.Clear();
                }
            }
            else
            {
                // act like not found in fallback
                FallbackDone(p, BillingFallbackResult.NotFound);
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

        void IClientConnectionHandler.HandlePacket(byte[] pkt, int len)
        {
            if (pkt == null || len < 1)
                return;

            lock (_lockObj)
            {
                _logManager.LogM(LogLevel.Info, nameof(BillingUdp), $"HandlePacket(0x{pkt[0]:X2},{len})");

                // Move past WaitLogin on any packet.
                if (_state == BillingState.WaitLogin)
                    LoggedIn();

                switch ((B2SPacketType)pkt[0])
                {
                    case B2SPacketType.UserLogin:
                        ProcessUserLogin(pkt, len);
                        break;

                    case B2SPacketType.UserPrivateChat:
                        ProcessUserPrivateChat(pkt, len);
                        break;

                    case B2SPacketType.UserKickout:
                        ProcessUserKickout(pkt, len);
                        break;

                    case B2SPacketType.UserCommandChat:
                        ProcessUserCommandChat(pkt, len);
                        break;

                    case B2SPacketType.UserChannelChat:
                        ProcessUserChannelChat(pkt, len);
                        break;

                    case B2SPacketType.ScoreReset:
                        ProcessScoreReset(pkt, len);
                        break;

                    case B2SPacketType.UserPacket:
                        ProcessUserPacket(pkt, len);
                        break;

                    case B2SPacketType.BillingIdentity:
                        ProcessBillingIdentity(pkt, len);
                        break;

                    case B2SPacketType.UserMulticastChannelChat:
                        ProcessUserMulticastChannelChat(pkt, len);
                        break;

                    default:
                        _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Unsupported packet type {pkt[0]}.");
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

        private void Packet_RegData(Player p, byte[] data, int length)
        {
            if (p == null || p[_pdKey] is not PlayerData pd)
                return;

            if (length < 1 || length - 1 > S2B_UserDemographics.DataLength)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(BillingUdp), p, $"Invalid demographics packet length {length}.");
                return;
            }

            lock (_lockObj)
            {
                if (pd.HasDemographics)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(BillingUdp), p, "Duplicate demographics packet.");
                    return;
                }

                if (pd.IsKnownToBiller)
                {
                    S2B_UserDemographics packet = new(p.Id);
                    data.AsSpan(1, length - 1).CopyTo(packet.Data);
                    _networkClient.SendPacket(
                        _cc,
                        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1)).Slice(0, S2B_UserDemographics.LengthWithoutData + length - 1),
                        NetSendFlags.Reliable);

                    pd.HasDemographics = true;
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

                        if (_cc != null)
                        {
                            _state = BillingState.Connecting;
                            _logManager.LogM(LogLevel.Info, nameof(BillingUdp), $"Connecting to user database server at {ipAddressStr}:{port}.");
                        }
                        else if (_state != BillingState.Disabled)
                        {
                            _state = BillingState.Retry;
                        }
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
                        ReadOnlySpan<byte> packet = stackalloc byte[1] { (byte)S2BPacketType.Ping };
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

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (p == null || p[_pdKey] is not PlayerData pd)
                return;

            lock (_lockObj)
            {
                if (action == PlayerAction.Disconnect)
                {
                    if (pd.LoginData != null)
                    {
                        // Disconnected while waiting for auth.
                        if (pd.IsKnownToBiller)
                        {
                            _pendingAuths--;
                            _interruptedAuths++;
                        }

                        pd.RemoveLoginData();
                    }

                    _bannerUploadDictionary.Remove(p.Id);

                    if (pd.IsKnownToBiller)
                    {
                        S2B_UserLogoff packet = new(
                            p.Id,
                            0, // TODO: put real reason here
                            0, 0, 0, 0); // TODO: get real latency numbers

                        ReadOnlySpan<byte> packetBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1));

                        // TODO: score
                        //if (UpdateScore(p, ref pd.SavedScore))
                        //{
                        //    packet.Score = p.SavedScore;
                        //}
                        //else
                        {
                            packetBytes = packetBytes.Slice(0, S2B_UserLogoff.LengthWithoutScore);
                            _networkClient.SendPacket(_cc, packetBytes, NetSendFlags.Reliable);
                        }
                    }
                }
                else if (action == PlayerAction.LeaveArena && arena.IsPublic)
                {
                    // TODO: score stats
                }
            }
        }

        private void Callback_ChatMessage(Player p, ChatMessageType type, ChatSound sound, Player playerTo, short freq, ReadOnlySpan<char> message)
        {
            if (message.Length < 1)
                return;

            if (p == null || p[_pdKey] is not PlayerData pd)
                return;

            if (!pd.IsKnownToBiller)
                return;

            if (type == ChatMessageType.Chat)
            {
                S2B_UserChannelChat packet = new(p.Id);
                ReadOnlySpan<char> text = message;
                ReadOnlySpan<char> channel = text.GetToken(';', out ReadOnlySpan<char> remaining);

                // Note that this supports a channel name in place of the usual channel number.
                // e.g., ;foo;this is a message to the foo channel
                // Most billers probably don't support this feature yet.
                if (!channel.IsEmpty 
                    && StringUtils.DefaultEncoding.GetByteCount(channel) < packet.ChannelBytes.Length // < to allow for the null-terminator
                    && remaining.Length > 0) // found ;
                {
                    packet.Channel = channel;
                    text = remaining[1..]; // skip the ;
                }
                else
                {
                    packet.Channel = "1";
                }

                int bytesWritten = packet.SetText(text);

                lock (_lockObj)
                {
                    _networkClient.SendPacket(
                        _cc,
                        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1)).Slice(0, S2B_UserChannelChat.GetLength(bytesWritten)),
                        NetSendFlags.Reliable);
                }
            }
            else if (type == ChatMessageType.RemotePrivate && playerTo == null) // remote private message to a player not on the server
            {
                S2B_UserPrivateChat packet = new(
                    -1, // for some odd reason ConnectionID >= 0 indicates global broadcast message
                    1, 
                    2, 
                    (byte)sound);

                ReadOnlySpan<char> text = message;
                ReadOnlySpan<char> toName = text.GetToken(':', out ReadOnlySpan<char> remaining);
                if (toName.IsEmpty || remaining.Length < 1)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(BillingUdp), p, "Malformed remote private message");
                }
                else
                {
                    StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                    int bytesWritten;

                    try
                    {
                        sb.Append($":{toName}:({p.Name})>{remaining[1..]}");

                        Span<char> textBuffer = stackalloc char[Math.Min(S2B_UserPrivateChat.MaxTextChars, sb.Length)];
                        sb.CopyTo(0, textBuffer, textBuffer.Length);

                        bytesWritten = packet.SetText(textBuffer);
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                    }

                    lock (_lockObj)
                    {
                        _networkClient.SendPacket(
                            _cc,
                            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1)).Slice(0, S2B_UserPrivateChat.GetLength(bytesWritten)),
                            NetSendFlags.Reliable);
                    }
                }
            }
        }

        private void Callback_SetBanner(Player p, in Banner banner, bool isFromPlayer)
        {
            if (!isFromPlayer)
                return;

            if (p == null || p[_pdKey] is not PlayerData pd)
                return;

            lock (_lockObj)
            {
                if (!pd.IsKnownToBiller)
                    return;

                _bannerUploadDictionary[p.Id] = new S2B_UserBanner(p.Id, banner);
            }
        }

        #endregion

        #region Command Handlers

        [CommandHelp(
            Targets = CommandTarget.Player  | CommandTarget.None,
            Args = null,
            Description =
            "Displays the usage information (current hours and minutes logged in, and\n" +
            "total hours and minutes logged in), as well as the first login time, of\n" +
            "the target player, or you if no target.")]
        private void Command_usage(string commandName, string parameters, Player p, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
                targetPlayer = p;
            
            if (targetPlayer?[_pdKey] is not PlayerData pd)
                return;

            if (!pd.IsKnownToBiller)
            {
                _chat.SendMessage(p, $"Usage unknown for {targetPlayer.Name}.");
                return;
            }

            _chat.SendMessage(p, $"Usage: {targetPlayer.Name}");
            _chat.SendMessage(p, $"session: {DateTime.UtcNow - targetPlayer.ConnectTime}");
            _chat.SendMessage(p, $"  total: {pd.Usage}");
            _chat.SendMessage(p, $"first played: {pd.FirstLogin}");
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.None,
            Args = null,
            Description =
            "Displays the user database id of the target player, or yours if no target.")]
        private void Command_userid(string commandName, string parameters, Player p, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
                targetPlayer = p;

            if (targetPlayer?[_pdKey] is not PlayerData pd)
                return;

            if (!pd.IsKnownToBiller)
            {
                _chat.SendMessage(p, $"User ID unknown for {targetPlayer.Name}");
                return;
            }

            _chat.SendMessage(p, $"{targetPlayer.Name} has User ID {pd.BillingUserId}");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "status|drop|connect",
            Description =
            "The subcommand 'status' reports the status of the user database server\n" +
            "connection. 'drop' disconnects the connection if it's up, and 'connect'\n" +
            "reconnects after dropping or failed login.")]
        private void Command_userdbadm(string commandName, string parameters, Player p, ITarget target)
        {
            lock (_lockObj)
            {
                if (string.Equals(parameters, "drop", StringComparison.OrdinalIgnoreCase))
                {
                    DropConnection(BillingState.Disabled);
                    _chat.SendMessage(p, "User database connection disabled.");
                }
                else if (string.Equals(parameters, "connect", StringComparison.OrdinalIgnoreCase))
                {
                    if (_state == BillingState.LoginFailed || _state == BillingState.Disabled || _state == BillingState.Retry)
                    {
                        _state = BillingState.NoSocket;
                        _chat.SendMessage(p, "User database connection reactivated.");
                    }
                    else
                    {
                        _chat.SendMessage(p, "User database server connection already active.");
                    }
                }
                else
                {
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

                    _chat.SendMessage(p, $"User database status: {status}  Pending auths: {_pendingAuths}.");

                    if (_identity != null && _identity.Length > 0)
                    {
                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                        try
                        {
                            sb.Append("Identity: ");

                            foreach(byte b in _identity)
                            {
                                if (sb.Length > 10)
                                    sb.Append(' ');

                                sb.Append($"{b:X2}");
                            }

                            _chat.SendMessage(p, sb);
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }
                    }
                }
            }
        }

        private void DefaultCommandReceived(string commandName, string line, Player p, ITarget target)
        {
            if (p == null || p[_pdKey] is not PlayerData pd)
                return;

            if (!pd.IsKnownToBiller)
                return;

            if (target.Type != TargetType.Arena)
            {
                _logManager.LogP(LogLevel.Drivel, nameof(BillingUdp), p, $"Unknown command with bad target: '{line}'.");
                return;
            }

            if (_chat.GetPlayerChatMask(p).IsRestricted(ChatMessageType.BillerCommand))
                return;

            S2B_UserCommand packet = new(p.Id);
            int len;

            if (line.StartsWith("chat=", StringComparison.OrdinalIgnoreCase) || line.StartsWith("chat ", StringComparison.OrdinalIgnoreCase))
            {
                len = RewriteChatCommand(p, line, ref packet);
            }
            else
            {
                len = packet.SetText(line, true);
            }

            _logManager.LogP(LogLevel.Info, nameof(BillingUdp), p, $"Sending command: {packet.TextBytes.ReadNullTerminatedString()}");

            lock (_lockObj)
            {
                _networkClient.SendPacket(
                    _cc,
                    MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1)).Slice(0, len),
                    NetSendFlags.Reliable);
            }
        }

        [ConfigHelp("Billing", "StaffChats", ConfigScope.Global, typeof(string), Description = "Comma separated staff chat list.")]
        [ConfigHelp("Billing", "StaffChatPrefix", ConfigScope.Global, typeof(string), Description = "Secret prefix to prepend to staff chats.")]
        [ConfigHelp("Billing", "LocalChats", ConfigScope.Global, typeof(string), Description = "Comma separated local chat list.")]
        [ConfigHelp("Billing", "LocalChatPrefix", ConfigScope.Global, typeof(string), Description = "Secret prefix to prepend to local chats.")]
        private int RewriteChatCommand(Player p, ReadOnlySpan<char> line, ref S2B_UserCommand packet)
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

            line = line[5..]; // skip "?chat=" or "?chat "

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                sb.Append("?chat=");

                ReadOnlySpan<char> chatName = ReadOnlySpan<char>.Empty;
                while ((chatName = line.GetToken(',', out line)).Length > 0)
                {
                    chatName = chatName.Trim();
                    if (chatName.Length <= 0 || chatName.Length > 31)
                        continue;

                    bool addComma = sb.Length > 6;

                    if (!localChats.IsWhiteSpace()
                        && (localPrefix.Length + 4 + chatName.Length) <= 31
                        && (localPrefix.Length + 4 + chatName.Length + sb.Length + (addComma ? 1 : 0)) <= (S2B_UserCommand.MaxTextChars - 1)
                        && FindChat(chatName, localChats))
                    {
                        if (addComma)
                            sb.Append(',');

                        sb.Append("$l$");
                        sb.Append(localPrefix);
                        sb.Append('|');
                    }
                    else if (!staffChats.IsWhiteSpace()
                        && (staffPrefix.Length + 4 + chatName.Length) <= 31
                        && (staffPrefix.Length + 4 + chatName.Length + sb.Length + (addComma ? 1 : 0)) <= (S2B_UserCommand.MaxTextChars - 1)
                        && _capabilityManager.HasCapability(p, Constants.Capabilities.SendModChat)
                        && FindChat(chatName, staffChats))
                    {
                        if (addComma)
                            sb.Append(',');

                        sb.Append("$s$");
                        sb.Append(staffPrefix);
                        sb.Append('|');
                    }

                    sb.Append(chatName);
                }

                Span<char> textBuffer = stackalloc char[Math.Min(S2B_UserCommand.MaxTextChars - 1, sb.Length)];
                sb.CopyTo(0, textBuffer, textBuffer.Length);
                return packet.SetText(textBuffer, false);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        #endregion

        private void FallbackDone(Player p, BillingFallbackResult result)
        {
            if (p == null || p[_pdKey] is not PlayerData pd)
                return;

            if (pd.LoginData == null)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), "Unexpected billing fallback response.");
                return;
            }

            AuthData authData = new();

            if (result == BillingFallbackResult.Match)
            {
                // Correct password, player is ok and authenticated.
                authData.Code = AuthCode.OK;
                authData.Authenticated = true;
                authData.Name = authData.SendName = pd.LoginData.Value.Name;
                authData.Squad = null;
                _logManager.LogP(LogLevel.Info, nameof(BillingUdp), p, "Fallback: authenticated.");
            }
            else if (result == BillingFallbackResult.NotFound)
            {
                // Add ^ in front of name and accept as unathenticated.
                authData.Code = AuthCode.OK;
                authData.Authenticated = false;
                authData.Name = authData.SendName = '^' + pd.LoginData.Value.Name;
                pd.IsKnownToBiller = false;
                _logManager.LogP(LogLevel.Info, nameof(BillingUdp), p, "Fallback: no entry for this player.");
            }
            else // Mismatch or anything else
            {
                authData.Code = AuthCode.BadPassword;
                authData.Authenticated = false;
                _logManager.LogP(LogLevel.Info, nameof(BillingUdp), p, "Fallback: invalid password.");
            }

            pd.LoginData.Value.DoneCallback(p, authData);
            pd.RemoveLoginData();
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
            // Only announce if changingg from LoggedIn
            if (_state == BillingState.LoggedIn)
            {
                _chat.SendArenaMessage((Arena)null, "Notice: Connection to user database server lost.");
            }

            // Clear KnownToBiller values
            _playerData.Lock();

            try
            {
                foreach (Player p in _playerData.PlayerList)
                {
                    if (p[_pdKey] is PlayerData pd)
                    {
                        pd.IsKnownToBiller = false;
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            // Close the client connection
            if (_cc != null)
            {
                // Ideally this would be sent reliably, but reliable packets won't get sent after DropConnection.
                ReadOnlySpan<byte> disconnect = stackalloc byte[1] { (byte)S2BPacketType.ServerDisconnect };
                _networkClient.SendPacket(_cc, disconnect, NetSendFlags.PriorityP5);
                _networkClient.DropConnection(_cc);
                _cc = null;
            }

            _state = newState;
            _lastEvent = DateTime.UtcNow;
        }

        #region B2S packet handlers

        private void ProcessUserLogin(byte[] data, int len)
        {
            if (data == null)
                return;

            if (len < B2S_UserLogin.LengthWithoutScore)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserLogin)} packet length ({len}).");
                return;
            }

            ref B2S_UserLogin packet = ref MemoryMarshal.AsRef<B2S_UserLogin>(data);

            Player p = _playerData.PidToPlayer(packet.ConnectionId);
            if (p == null)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Biller sent {nameof(B2S_UserLogin)} for unknown pid {packet.ConnectionId}.");
                return;
            }

            if (p[_pdKey] is not PlayerData pd)
                return;

            if (pd.LoginData == null)
            {
                _logManager.LogP(LogLevel.Warn, nameof(BillingUdp), p, $"Unexpected {nameof(B2S_UserLogin)} response.");
                return;
            }

            AuthData authData = new();

            if (packet.Result == B2SUserLoginResult.Ok
                || packet.Result == B2SUserLoginResult.DemoVersion
                || packet.Result == B2SUserLoginResult.AskDemographics)
            {
                pd.FirstLogin = packet.FirstLogin.ToDateTime();
                pd.Usage = packet.Usage;
                pd.BillingUserId = packet.UserId;

                // TODO: Stats - Note: ASSS has this commented out
                //if (len >= B2S_UserLogin.LengthWithScore)
                //{
                //    pd.SavedScore = packet.Score;
                //    pd.SetPublicScore = true;
                //}
                pd.SavedScore = default;

                authData.DemoData = packet.Result == B2SUserLoginResult.AskDemographics || packet.Result == B2SUserLoginResult.DemoVersion;
                authData.Code = authData.DemoData ? AuthCode.AskDemographics : AuthCode.OK;
                authData.Authenticated = true;
                authData.Name = authData.SendName = packet.NameBytes.ReadNullTerminatedString();
                authData.Squad = packet.SquadBytes.ReadNullTerminatedString();

                if (packet.Banner.IsSet)
                {
                    IBanners banners = _broker.GetInterface<IBanners>();
                    if (banners != null)
                    {
                        try
                        {
                            banners.SetBanner(p, in packet.Banner, false);
                        }
                        finally
                        {
                            _broker.ReleaseInterface(ref banners);
                        }
                    }
                }

                _logManager.LogP(LogLevel.Info, nameof(BillingUdp), p, $"Player authenticated {packet.Result} with user id {packet.UserId}.");
            }
            else
            {
                authData.DemoData = false;

                authData.Code = packet.Result switch
                {
                    B2SUserLoginResult.NewUser => AuthCode.NewName,
                    B2SUserLoginResult.InvalidPw => AuthCode.BadPassword,
                    B2SUserLoginResult.Banned => AuthCode.LockedOut,
                    B2SUserLoginResult.NoNewConns => AuthCode.NoNewConn,
                    B2SUserLoginResult.BadUserName => AuthCode.BadName,
                    B2SUserLoginResult.ServerBusy => AuthCode.ServerBusy,
                    _ => AuthCode.NoPermission,
                };

                authData.Authenticated = false;

                _logManager.LogP(LogLevel.Info, nameof(BillingUdp), p, $"Player rejected ({packet.Result} / {authData.Code}).");
            }

            pd.LoginData.Value.DoneCallback(p, authData);
            pd.RemoveLoginData();
            _pendingAuths--;
        }

        private void ProcessUserPrivateChat(byte[] pkt, int len)
        {
            if (len < B2S_UserPrivateChat.MinLength || len > B2S_UserPrivateChat.MaxLength)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserPrivateChat)} - length ({len}).");
                return;
            }

            ref B2S_UserPrivateChat packet = ref MemoryMarshal.AsRef<B2S_UserPrivateChat>(pkt);

            if (packet.SubType != 2)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserPrivateChat)} - SubType ({packet.SubType}).");
                return;
            }

            Span<byte> textBytes = packet.GetTextBytes(len);
            int index = textBytes.IndexOf((byte)0);
            if(index == -1)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserPrivateChat)} - Text not null-terminated.");
                return;
            }

            textBytes = textBytes.Slice(0, index);
            Span<char> text = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(textBytes)];
            StringUtils.DefaultEncoding.GetChars(textBytes, text);

            if (text.Length >= 1 && text[0] == ':') // private message
            {
                // HACK: the compiler wouldn't allow use of StringUtils.GetToken(...) with the stack allocated Span<char>, but it does if it's wrapped in another method. Review this later...
                ProcessRemotePrivateMessage(in packet, text);
            }
            else // broadcast message
            {
                _chat.SendArenaMessage((Arena)null, (ChatSound)packet.Sound, text);
            }
        }

        private void ProcessRemotePrivateMessage(in B2S_UserPrivateChat packet, Span<char> text)
        {
            // Remote private messages should be in the format:
            // :recpient:(sender)>message
            // recipient can be a squad, in which case it begins with a #
            // if sender has )> in their name, then too bad, we take the first )>

            if (text.Length < 3) // minimum of a 1 character recipient and no message
                return;

            Span<char> recipient = text.GetToken(':', out Span<char> remaining);
            if (recipient.Length < 1 || remaining.Length < 4 || remaining[1] != '(')
                return;

            remaining = remaining[2..]; // skip the :

            int index = remaining.IndexOf(")>");
            if (index == -1 || index == 0 || index > 30)
                return;

            Span<char> sender = remaining.Slice(0, index);
            remaining = remaining[(index + 2)..];

            if (recipient[0] == '#')
            {
                // squad message
                recipient = recipient[1..];
                if (recipient.Length < 1)
                    return;

                HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    _playerData.Lock();

                    try
                    {
                        foreach (Player p in _playerData.PlayerList)
                            if (MemoryExtensions.Equals(p.Squad, recipient, StringComparison.OrdinalIgnoreCase))
                                set.Add(p);
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }

                    if (set.Count == 0)
                        return;

                    _chat.SendRemotePrivMessage(set, (ChatSound)packet.Sound, recipient, sender, remaining);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(set);
                }
            }
            else
            {
                Player p = _playerData.FindPlayer(recipient);
                if (p == null)
                    return;

                HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    set.Add(p);
                    _chat.SendRemotePrivMessage(set, (ChatSound)packet.Sound, null, sender, remaining);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(set);
                }
            }
        }

        private void ProcessUserKickout(byte[] pkt, int len)
        {
            if (len < B2S_UserKickout.Length)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserKickout)} packet length {len}.");
                return;
            }

            ref B2S_UserKickout packet = ref MemoryMarshal.AsRef<B2S_UserKickout>(pkt);

            Player p = _playerData.PidToPlayer(packet.ConnectionId);
            if (p != null)
            {
                _playerData.KickPlayer(p);
                _logManager.LogP(LogLevel.Info, nameof(BillingUdp), p, $"Player kicked out by user database server ({packet.Reason}).");
            }
        }

        private void ProcessUserCommandChat(byte[] pkt, int len)
        {
            if (len < B2S_UserCommandChat.MinLength || len > B2S_UserCommandChat.MaxLength)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserCommandChat)} - length ({len}).");
                return;
            }

            ref B2S_UserCommandChat packet = ref MemoryMarshal.AsRef<B2S_UserCommandChat>(pkt);

            Player p = _playerData.PidToPlayer(packet.ConnectionId);
            if (p == null)
            {
                _logManager.LogM(LogLevel.Info, nameof(BillingUdp), $"Invalid {nameof(B2S_UserCommandChat)} - player not found ({packet.ConnectionId}).");
                return;
            }

            Span<byte> textBytes = packet.GetTextBytes(len);
            int index = textBytes.IndexOf((byte)0);
            if (index == -1)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserCommandChat)} - Text not null-terminated.");
                return;
            }

            textBytes = textBytes.Slice(0, index);
            Span<char> text = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(textBytes)];
            StringUtils.DefaultEncoding.GetChars(textBytes, text);

            index = text.IndexOf('|'); // local and staff chats have a pipe appended
            if (index != -1 && text.Length > 3 && MemoryExtensions.Equals(text[..3], "$l$", StringComparison.Ordinal))
            {
                _chat.SendMessage(p, $"(local) {text[(index + 1)..]}");
            }
            else if (index != -1 && text.Length > 3 && MemoryExtensions.Equals(text[..3], "$s$", StringComparison.Ordinal))
            {
                _chat.SendMessage(p, $"(staff) {text[(index + 1)..]}");
            }
            else
            {
                _chat.SendMessage(p, text);
            }
        }

        private void ProcessUserChannelChat(byte[] pkt, int len)
        {
            if (len < B2S_UserChannelChat.MinLength || len > B2S_UserChannelChat.MaxLength)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserChannelChat)} - length ({len}).");
                return;
            }

            ref B2S_UserChannelChat packet = ref MemoryMarshal.AsRef<B2S_UserChannelChat>(pkt);

            Player p = _playerData.PidToPlayer(packet.ConnectionId);
            if (p == null)
            {
                _logManager.LogM(LogLevel.Info, nameof(BillingUdp), $"Invalid {nameof(B2S_UserChannelChat)} - player not found ({packet.ConnectionId}).");
                return;
            }

            Span<byte> textBytes = packet.GetTextBytes(len);
            int index = textBytes.IndexOf((byte)0);
            if (index == -1)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserChannelChat)} - Text not null-terminated.");
                return;
            }

            textBytes = textBytes.Slice(0, index);

            int numChars = StringUtils.DefaultEncoding.GetCharCount(textBytes);
            Span<char> messageBuffer = stackalloc char[3 + 1 + numChars]; // enough for channel + ':' + text
            if (!packet.Channel.TryFormat(messageBuffer, out int charsWritten))
                return;

            messageBuffer[charsWritten++] = ':';
            Span<char> textBuffer = messageBuffer[charsWritten..];
            StringUtils.DefaultEncoding.GetChars(textBytes, textBuffer);
            messageBuffer = messageBuffer.Slice(0, charsWritten + numChars);

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                set.Add(p);
                _chat.SendAnyMessage(set, ChatMessageType.Chat, ChatSound.None, null, messageBuffer);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        private void ProcessScoreReset(byte[] pkt, int len)
        {
            if (len < B2S_ScoreReset.Length)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_ScoreReset)} - length ({len}).");
                return;
            }

            ref B2S_ScoreReset packet = ref MemoryMarshal.AsRef<B2S_ScoreReset>(pkt);
            if (packet.ScoreId != -packet.ScoreIdNegative)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_ScoreReset)} - ScoreId mismatch ({packet.ScoreId}/{packet.ScoreIdNegative}).");
                return;
            }

            if (_configManager.GetInt(_configManager.Global, "Billing", "HonorScoreResetRequests", 1) != 0)
            {
                IPersist persist = _broker.GetInterface<IPersist>();

                if (persist != null)
                {
                    try
                    {
                        // Reset scores in public arenas.
                        // TODO: persist.EndInterval()
                        _logManager.LogM(LogLevel.Info, nameof(BillingUdp), "Billing server requested score reset, resetting scores.");
                    }
                    finally
                    {
                        _broker.ReleaseInterface(ref persist);
                    }
                }
                else
                {
                    _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), "Billing server requested score reset, but IPersist is not available.");
                }
            }
            else
            {
                _logManager.LogM(LogLevel.Info, nameof(BillingUdp), "Billing server requested score reset, but honoring such requests is disabled (Billing:HonorScoreResetRequests).");
            }
        }

        private void ProcessUserPacket(byte[] pkt, int len)
        {
            if (len < B2S_UserPacket.MinLength || len > B2S_UserPacket.MaxLength)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserPacket)} - length ({len}).");
                return;
            }

            ref B2S_UserPacket packet = ref MemoryMarshal.AsRef<B2S_UserPacket>(pkt);
            Span<byte> dataBytes = packet.GetDataBytes(len);
            if (dataBytes.IsEmpty)
                return; // sanity, MinLength check should prevent this already

            if (packet.ConnectionId == -1)
            {
                // sent to all players not allowed
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"{nameof(B2S_UserPacket)} filtered (target all).");
                return;
            }

            Player p = _playerData.PidToPlayer(packet.ConnectionId);
            if (p == null)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"{nameof(B2S_UserPacket)} unknown pid ({packet.ConnectionId}).");
                return;
            }

            // Only allow S2CPacketType.LoginText for banned players to get the ban text.
            if (dataBytes[0] == (byte)S2CPacketType.LoginText)
            {
                _network.SendToOne(p, dataBytes, NetSendFlags.Reliable);
            }
            else
            {
                _logManager.LogP(LogLevel.Warn, nameof(BillingUdp), p, $"{nameof(B2S_UserPacket)} filtered (type: 0x{dataBytes[0]:X2}).");
            }
        }

        private void ProcessBillingIdentity(byte[] pkt, int len)
        {
            if (len < 1)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_BillingIdentity)} - length ({len}).");
                return;
            }

            ref B2S_BillingIdentity packet = ref MemoryMarshal.AsRef<B2S_BillingIdentity>(pkt);
            Span<byte> identityBytes = packet.GetDataBytes(len);
            _identity = identityBytes.ToArray();
        }

        private void ProcessUserMulticastChannelChat(byte[] pkt, int len)
        {
            if (len < B2S_UserMulticastChannelChat.MinLength || len > B2S_UserMulticastChannelChat.MaxLength)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserMulticastChannelChat)} - length ({len}).");
                return;
            }

            ref B2S_UserMulticastChannelChat packet = ref MemoryMarshal.AsRef<B2S_UserMulticastChannelChat>(pkt);
            if (packet.Count < 1)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserMulticastChannelChat)} - {packet.Count} recipients.");
                return;
            }

            ReadOnlySpan<byte> textBytes = packet.GetTextBytes(len);
            if (textBytes.IsEmpty)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserMulticastChannelChat)} - length ({len}) for {packet.Count} recipients.");
                return;
            }

            int index = textBytes.IndexOf((byte)0);
            if (index == -1)
            {
                _logManager.LogM(LogLevel.Warn, nameof(BillingUdp), $"Invalid {nameof(B2S_UserMulticastChannelChat)} - Text not null-terminated.");
                return;
            }

            textBytes = textBytes.Slice(0, index);

            Span<char> text = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(textBytes)];
            StringUtils.DefaultEncoding.GetChars(textBytes, text);

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                ReadOnlySpan<MChannelChatRecipient> recipients = packet.GetRecipients(len);
                Span<char> messageBuffer = stackalloc char[250];
                for (int i = 0; i < recipients.Length; i++)
                {
                    Player p = _playerData.PidToPlayer(recipients[i].ConnectionId);
                    if (p == null)
                        continue;

                    set.Clear();
                    set.Add(p);

                    sb.Clear();
                    sb.Append(recipients[0].Channel);
                    sb.Append(':');
                    sb.Append(text);

                    int numCharacters = sb.Length;
                    if (numCharacters > messageBuffer.Length)
                        numCharacters = messageBuffer.Length;

                    sb.CopyTo(0, messageBuffer, numCharacters);

                    Span<char> message = messageBuffer.Slice(0, numCharacters);

                    _chat.SendAnyMessage(set, ChatMessageType.Chat, ChatSound.None, null, message);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        #endregion

        private struct LoginData
        {
            public AuthDoneDelegate DoneCallback;
            public string Name;

            public void Clear()
            {
                DoneCallback = null;
                Name = null;
            }
        }

        private class PlayerData
        {
            public uint BillingUserId;
            public TimeSpan Usage;
            public bool IsKnownToBiller;
            public bool HasDemographics;
            public LoginData? LoginData;
            public DateTime? FirstLogin;
            public PlayerScore SavedScore;

            public void RemoveLoginData()
            {
                if (LoginData != null)
                {
                    LoginData.Value.Clear();
                    LoginData = null;
                }
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

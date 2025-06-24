using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GlobalChatSettings = SS.Core.ConfigHelp.Constants.Global.Chat;
using SSProto = SS.Core.Persist.Protobuf;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides chat functionality.
    /// </summary>
    /// <remarks>
    /// This includes everything that a player can type including:
    /// <list type="bullet">
    /// <item>public messages</item>
    /// <item>private messages</item>
    /// <item>remote private messages</item>
    /// <item>team messages</item>
    /// <item>enemy team messages</item>
    /// <item>chat channels</item>
    /// <item>moderator chat</item>
    /// <item>commands</item>
    /// </list>
    /// </remarks>
    [CoreModuleInfo]
    public sealed class Chat(
        IComponentBroker broker,
        IArenaManager arenaManager,
        ICapabilityManager capabilityManager,
        ICommandManager commandManager,
        IConfigManager configManager,
        ILogManager logManager,
        IObjectPoolManager objectPoolManager,
        IPlayerData playerData) : IAsyncModule, IChat, IStringBuilderPoolProvider
    {
        private const char CmdChar1 = '?';
        private const char CmdChar2 = '*';
        private const char MultiChar = '|';
        private const char ModChatChar = '\\';

        // required dependencies
        private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        private readonly IArenaManager _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        private readonly ICapabilityManager _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
        private readonly ICommandManager _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(broker));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        // optional dependencies
        private IChatNetwork? _chatNetwork;
        private INetwork? _network;
        private IObscene? _obscene;
        private IPersist? _persist;

        private InterfaceRegistrationToken<IChat>? _iChatToken;

        private ArenaDataKey<ArenaData> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;

        private Config _cfg;
        private DelegatePersistentData<Player>? _persistRegistration;

        #region Module Members

        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _network = broker.GetInterface<INetwork>();
            _chatNetwork = broker.GetInterface<IChatNetwork>();

            if (_network is null && _chatNetwork is null)
            {
                // need at least one of the network interfaces
                _logManager.LogM(LogLevel.Error, nameof(Chat), "Failed to get at least one of the network interface dependencies (INetwork or IChatNetwork).");
                return false;
            }

            _persist = broker.GetInterface<IPersist>();
            _obscene = broker.GetInterface<IObscene>();

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            if (_persist is not null)
            {
                _persistRegistration = new DelegatePersistentData<Player>(
                    (int)PersistKey.Chat, PersistInterval.ForeverNotShared, PersistScope.PerArena, Persist_GetData, Persist_SetData, Persist_ClearData);
                await _persist.RegisterPersistentDataAsync(_persistRegistration);
            }

            _cfg = new Config(_configManager);

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            PlayerActionCallback.Register(_broker, Callback_PlayerAction);

            _network?.AddPacket(C2SPacketType.Chat, Packet_Chat);
            _chatNetwork?.AddHandler("SEND", ChatHandler_Send);

            _iChatToken = _broker.RegisterInterface<IChat>(this);

            return true;
        }

        async Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (broker.UnregisterInterface(ref _iChatToken) != 0)
                return false;

            _network?.RemovePacket(C2SPacketType.Chat, Packet_Chat);
            _chatNetwork?.RemoveHandler("SEND", ChatHandler_Send);

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);

            if (_persist is not null && _persistRegistration is not null)
                await _persist.UnregisterPersistentDataAsync(_persistRegistration);

            _arenaManager.FreeArenaData(ref _adKey);
            _playerData.FreePlayerData(ref _pdKey);

            if (_persist is not null)
                _broker.ReleaseInterface(ref _persist);

            if (_obscene is not null)
                _broker.ReleaseInterface(ref _obscene);

            if (_network is not null)
                _broker.ReleaseInterface(ref _network);

            if (_chatNetwork is not null)
                _broker.ReleaseInterface(ref _chatNetwork);

            return true;
        }

        #endregion

        #region IChat Members

        void IChat.SendMessage(Player player, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IChat)this).SendMessage(player, ChatSound.None, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChat.SendMessage(Player player, IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((IChat)this).SendMessage(player, ref handler);
        }

        void IChat.SendMessage(Player player, ReadOnlySpan<char> message)
        {
            ((IChat)this).SendMessage(player, ChatSound.None, message);
        }

        void IChat.SendMessage(Player player, string message)
        {
            ((IChat)this).SendMessage(player, ChatSound.None, message.AsSpan());
        }

        void IChat.SendMessage(Player player, StringBuilder message)
        {
            ((IChat)this).SendMessage(player, ChatSound.None, message);
        }

        void IChat.SendMessage(Player player, ChatSound sound, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IChat)this).SendMessage(player, sound, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChat.SendMessage(Player player, ChatSound sound, IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((IChat)this).SendMessage(player, sound, ref handler);
        }

        void IChat.SendMessage(Player player, ChatSound sound, ReadOnlySpan<char> message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                set.Add(player);
                SendMessage(set, ChatMessageType.Arena, sound, null, message);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        void IChat.SendMessage(Player player, ChatSound sound, string message)
        {
            ((IChat)this).SendMessage(player, sound, message.AsSpan());
        }

        void IChat.SendMessage(Player player, ChatSound sound, StringBuilder message)
        {
            Span<char> text = stackalloc char[Math.Min(ChatPacket.MaxMessageChars, message.Length)];
            message.CopyTo(0, text, text.Length);
            ((IChat)this).SendMessage(player, sound, text);
        }

        void IChat.SendSetMessage(HashSet<Player> set, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IChat)this).SendSetMessage(set, ChatSound.None, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChat.SendSetMessage(HashSet<Player> set, IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((IChat)this).SendSetMessage(set, ref handler);
        }

        void IChat.SendSetMessage(HashSet<Player> set, ReadOnlySpan<char> message)
        {
            ((IChat)this).SendSetMessage(set, ChatSound.None, message);
        }

        void IChat.SendSetMessage(HashSet<Player> set, string message)
        {
            ((IChat)this).SendSetMessage(set, ChatSound.None, message.AsSpan());
        }

        void IChat.SendSetMessage(HashSet<Player> set, StringBuilder message)
        {
            ((IChat)this).SendSetMessage(set, ChatSound.None, message);
        }

        void IChat.SendSetMessage(HashSet<Player> set, ChatSound sound, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IChat)this).SendSetMessage(set, sound, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChat.SendSetMessage(HashSet<Player> set, ChatSound sound, IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((IChat)this).SendSetMessage(set, sound, ref handler);
        }

        void IChat.SendSetMessage(HashSet<Player> set, ChatSound sound, ReadOnlySpan<char> message)
        {
            SendMessage(set, ChatMessageType.Arena, sound, null, message);
        }

        void IChat.SendSetMessage(HashSet<Player> set, ChatSound sound, string message)
        {
            ((IChat)this).SendSetMessage(set, sound, message.AsSpan());
        }

        void IChat.SendSetMessage(HashSet<Player> set, ChatSound sound, StringBuilder message)
        {
            Span<char> text = stackalloc char[Math.Min(ChatPacket.MaxMessageChars, message.Length)];
            message.CopyTo(0, text, text.Length);
            ((IChat)this).SendSetMessage(set, sound, text);
        }

        void IChat.SendArenaMessage(Arena? arena, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IChat)this).SendArenaMessage(arena, ChatSound.None, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChat.SendArenaMessage(Arena? arena, IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((IChat)this).SendArenaMessage(arena, ref handler);
        }

        void IChat.SendArenaMessage(Arena? arena, ReadOnlySpan<char> message)
        {
            ((IChat)this).SendArenaMessage(arena, ChatSound.None, message);
        }

        void IChat.SendArenaMessage(Arena? arena, string message)
        {
            ((IChat)this).SendArenaMessage(arena, ChatSound.None, message.AsSpan());
        }

        void IChat.SendArenaMessage(Arena? arena, StringBuilder message)
        {
            ((IChat)this).SendArenaMessage(arena, ChatSound.None, message);
        }

        void IChat.SendArenaMessage(Arena? arena, ChatSound sound, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IChat)this).SendArenaMessage(arena, sound, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChat.SendArenaMessage(Arena? arena, ChatSound sound, IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((IChat)this).SendArenaMessage(arena, sound, ref handler);
        }

        void IChat.SendArenaMessage(Arena? arena, ChatSound sound, ReadOnlySpan<char> message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetArenaSet(set, arena, null);

                if (set.Count > 0)
                {
                    SendMessage(set, ChatMessageType.Arena, sound, null, message);
                }

                FireChatMessageCallback(arena, null, ChatMessageType.Arena, sound, null, -1, message);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        void IChat.SendArenaMessage(Arena? arena, ChatSound sound, string message)
        {
            ((IChat)this).SendArenaMessage(arena, sound, message.AsSpan());
        }

        void IChat.SendArenaMessage(Arena? arena, ChatSound sound, StringBuilder message)
        {
            Span<char> text = stackalloc char[Math.Min(ChatPacket.MaxMessageChars, message.Length)];
            message.CopyTo(0, text, text.Length);

            ((IChat)this).SendArenaMessage(arena, sound, text);
        }

        void IChat.SendAnyMessage(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player? from, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IChat)this).SendAnyMessage(set, type, sound, from, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChat.SendAnyMessage(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player? from, IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((IChat)this).SendAnyMessage(set, type, sound, from, ref handler);
        }

        void IChat.SendAnyMessage(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player? from, ReadOnlySpan<char> message)
        {
            SendMessage(set, type, sound, from, message);
        }

        void IChat.SendAnyMessage(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player? from, string message)
        {
            ((IChat)this).SendAnyMessage(set, type, sound, from, message.AsSpan());
        }

        void IChat.SendAnyMessage(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player? from, StringBuilder message)
        {
            Span<char> text = stackalloc char[Math.Min(ChatPacket.MaxMessageChars, message.Length)];
            message.CopyTo(0, text, text.Length);
            SendMessage(set, type, sound, from, text);
        }

        void IChat.SendModMessage(ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            try
            {
                ((IChat)this).SendModMessage(handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChat.SendModMessage(IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((IChat)this).SendModMessage(ref handler);
        }

        void IChat.SendModMessage(ReadOnlySpan<char> message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetCapabilitySet(set, Constants.Capabilities.ModChat, null);

                if (set.Count > 0)
                    SendMessage(set, ChatMessageType.SysopWarning, ChatSound.None, null, message);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        void IChat.SendModMessage(string message)
        {
            ((IChat)this).SendModMessage(message.AsSpan());
        }

        void IChat.SendModMessage(StringBuilder message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetCapabilitySet(set, Constants.Capabilities.ModChat, null);

                if (set.Count > 0)
                {
                    Span<char> text = stackalloc char[Math.Min(ChatPacket.MaxMessageChars, message.Length)];
                    message.CopyTo(0, text, text.Length);
                    SendMessage(set, ChatMessageType.SysopWarning, ChatSound.None, null, text);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        void IChat.SendRemotePrivMessage(HashSet<Player> set, ChatSound sound, ReadOnlySpan<char> squad, ReadOnlySpan<char> sender, ReadOnlySpan<char> message)
        {
            if (_network is not null)
            {
                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                try
                {
                    if (!squad.IsEmpty)
                    {
                        sb.Append($"(#{squad})");
                    }

                    sb.Append($"({sender})>{message}");

                    Span<char> text = stackalloc char[Math.Min(ChatPacket.MaxMessageChars, sb.Length)];
                    sb.CopyTo(0, text, text.Length);

                    Span<byte> chatBytes = stackalloc byte[ChatPacket.GetPacketByteCount(text)];
                    ref ChatPacket chatPacket = ref MemoryMarshal.AsRef<ChatPacket>(chatBytes);
                    chatPacket.Type = (byte)S2CPacketType.Chat;
                    chatPacket.ChatType = (byte)ChatMessageType.RemotePrivate;
                    chatPacket.Sound = (byte)sound;
                    chatPacket.PlayerId = -1;
                    int length = ChatPacket.SetMessage(chatBytes, text);

                    _network.SendToSet(set, chatBytes[..length], NetSendFlags.Reliable);
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }

            if (_chatNetwork is not null)
            {
                if (!squad.IsEmpty)
                {
                    _chatNetwork.SendToSet(set, $"MSG:SQUAD:{squad}:{sender}:{message}");
                }
                else
                {
                    _chatNetwork.SendToSet(set, $"MSG:PRIV:{sender}:{message}");
                }
            }
        }

        ChatMask IChat.GetArenaChatMask(Arena arena)
        {
            if (arena is null)
                return new ChatMask();

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return new ChatMask();

            ad.Lock.EnterReadLock();

            try
            {
                return ad.Mask;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        void IChat.SetArenaChatMask(Arena arena, ChatMask mask)
        {
            if (arena is null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            ad.Lock.EnterWriteLock();

            try
            {
                ad.Mask = mask;
            }
            finally
            {
                ad.Lock.ExitWriteLock();
            }
        }

        ChatMask IChat.GetPlayerChatMask(Player player)
        {
            if (player is null)
                return new ChatMask();

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return new ChatMask();

            lock (pd.Lock)
            {
                ExpireMask(player);
                return pd.Mask;
            }
        }

        void IChat.GetPlayerChatMask(Player player, out ChatMask mask, out TimeSpan? remaining)
        {
            if (player is null
                || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
            {
                mask = default;
                remaining = default;
                return;
            }

            lock (pd.Lock)
            {
                DateTime now = DateTime.UtcNow;

                ExpireMask(player);
                mask = pd.Mask;

                remaining = pd.Expires is null ? new TimeSpan?() : pd.Expires.Value - now;
            }
        }

        void IChat.SetPlayerChatMask(Player player, ChatMask mask, int timeout)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (pd.Lock)
            {
                pd.Mask = mask;

                if (timeout == 0 || mask.IsClear)
                    pd.Expires = null;
                else
                    pd.Expires = DateTime.UtcNow.AddSeconds(timeout);
            }
        }

        void IChat.SendWrappedText(Player player, string text)
        {
            if (player is null)
                return;

            if (string.IsNullOrWhiteSpace(text))
                return;

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                foreach (ReadOnlySpan<char> line in text.GetWrappedText(78, ' '))
                {
                    sb.Clear();
                    sb.Append("  ");
                    sb.Append(line);

                    ((IChat)this).SendMessage(player, sb);
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        void IChat.SendWrappedText(Player player, StringBuilder sb)
        {
            if (player is null)
                return;

            if (sb is null || sb.Length == 0)
                return;

            StringBuilder tempBuilder = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                foreach (ReadOnlySpan<char> line in sb.GetWrappedText(78, ' ')) // foreach handles calling Dispose() on the enumerator
                {
                    tempBuilder.Clear();
                    tempBuilder.Append("  ");
                    tempBuilder.Append(line);

                    ((IChat)this).SendMessage(player, tempBuilder);
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(tempBuilder);
            }
        }

        #endregion

        #region IStringBuilderPoolProvider

        ObjectPool<StringBuilder> IStringBuilderPoolProvider.StringBuilderPool => _objectPoolManager.StringBuilderPool;

        #endregion

        #region Persist methods

        private void Persist_GetData(Player? player, Stream outStream)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            SSProto.ChatMask protoChatMask;

            lock (pd.Lock)
            {
                if (pd.Expires is null)
                    return;

                protoChatMask = new();
                protoChatMask.Mask = pd.Mask.Value;
                protoChatMask.Expires = pd.Expires is not null ? Timestamp.FromDateTime(pd.Expires.Value) : null;
                protoChatMask.MessageCount = pd.MessageCount;
                protoChatMask.LastCheck = Timestamp.FromDateTime(pd.LastCheck);
            }

            protoChatMask.WriteTo(outStream);
        }

        private void Persist_SetData(Player? player, Stream inStream)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            SSProto.ChatMask protoChatMask = SSProto.ChatMask.Parser.ParseFrom(inStream);

            lock (pd.Lock)
            {
                pd.Mask = new ChatMask(protoChatMask.Mask);
                pd.Expires = protoChatMask.Expires?.ToDateTime();
                pd.MessageCount = protoChatMask.MessageCount;
                pd.LastCheck = protoChatMask.LastCheck.ToDateTime();
            }
        }

        private void Persist_ClearData(Player? player)
        {
            if (player is null || !player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (pd.Lock)
            {
                pd.Reset();
            }
        }

        #endregion

        [ConfigHelp<int>("Chat", "RestrictChat", ConfigScope.Arena, Default = 0,
            Description = "This specifies an initial chat mask for the arena. Don't use this unless you know what you're doing.")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena is null)
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                if (!arena.TryGetExtraData(_adKey, out ArenaData? arenaData))
                    return;

                arenaData.Mask = new(_configManager.GetInt(arena.Cfg!, "Chat", "RestrictChat", ConfigHelp.Constants.Arena.Chat.RestrictChat.Default));
            }
        }

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            if (action == PlayerAction.PreEnterArena)
            {
                pd.Reset();
            }
        }

        private void Packet_Chat(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (player is null)
                return;

            // Ideally we would check for len > ChatPacket.MaxLength (255 bytes), but Continuum seems to have a bug.
            // It allows sending typing > 250 characters and sends a chat packet larger than the max (up to 261 bytes),
            // though the text is null-terminated in the proper spot (limiting the message to 250 characters),
            // but with seemingly garbage data in the remaining bytes.
            // So, we'll allow 261, but only actually read up to the actual 250 characters.

            if (data.Length < ChatPacket.MinLength
                || data.Length > 261) // Note: for some reason ASSS checks if > 500 instead
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Chat), player, $"Bad chat packet (length={data.Length}).");
                return;
            }

            Arena? arena = player.Arena;
            if (arena is null || player.Status != PlayerState.Playing)
                return;

            ref readonly ChatPacket from = ref MemoryMarshal.AsRef<ChatPacket>(data);

            // Determine which bytes are part of the message.
            ReadOnlySpan<byte> messageBytes = ChatPacket.GetMessageBytes(data);

            // Decode the bytes.
            Span<char> text = stackalloc char[StringUtils.DefaultEncoding.GetMaxCharCount(messageBytes.Length)];
            int decodedCharCount = StringUtils.DefaultEncoding.GetChars(messageBytes, text);
            text = text[..decodedCharCount];

            // Remove control characters from the chat message.
            StringUtils.ReplaceControlCharacters(text);

            // Game clients (e.g. Continuum) can display only a subset of the printable, extended ASCII characters.
            // However, Continuum still sends the proper character if it is typed. It just displays a space in the place of such characters.
            // We purposely leave them in the message. Chat clients can display the characters.

            ChatSound sound = _capabilityManager.HasCapability(player, Constants.Capabilities.SoundMessages)
                ? (ChatSound)from.Sound
                : ChatSound.None;

            Player? target;
            switch ((ChatMessageType)from.ChatType)
            {
                case ChatMessageType.Arena:
                    _logManager.LogP(LogLevel.Malicious, nameof(Chat), player, "Received arena message.");
                    break;

                case ChatMessageType.PubMacro:
                case ChatMessageType.Pub:
                    if (text.Length > 0 && text[0] == ModChatChar)
                        HandleModChat(player, text[1..], sound);
                    else
                        HandlePub(player, text, from.ChatType == (byte)ChatMessageType.PubMacro, false, sound);
                    break;

                case ChatMessageType.EnemyFreq:
                    target = _playerData.PidToPlayer(from.PlayerId);
                    if (target is null)
                        break;

                    if (target.Arena == arena)
                        HandleFreq(player, target.Freq, text, sound);
                    else
                        _logManager.LogP(LogLevel.Malicious, nameof(Chat), player, "Received cross-arena enemy freq chat message.");
                    break;

                case ChatMessageType.Freq:
                    HandleFreq(player, player.Freq, text, sound);
                    break;

                case ChatMessageType.Private:
                    target = _playerData.PidToPlayer(from.PlayerId);
                    if (target is null)
                        break;

                    if (target.Arena == arena)
                        HandlePrivate(player, target, text, false, sound);
                    else
                        _logManager.LogP(LogLevel.Malicious, nameof(Chat), player, "Received cross-arena private chat message.");
                    break;

                case ChatMessageType.RemotePrivate:
                    HandleRemotePrivate(player, text, false, sound);
                    break;

                case ChatMessageType.SysopWarning:
                    _logManager.LogP(LogLevel.Malicious, nameof(Chat), player, "Received sysop message.");
                    break;

                case ChatMessageType.Chat:
                    HandleChat(player, text, sound);
                    break;

                default:
                    _logManager.LogP(LogLevel.Malicious, nameof(Chat), player, $"Received undefined chat message type {from.ChatType}.");
                    break;
            }

            CheckFlood(player);
        }

        private void ChatHandler_Send(Player player, ReadOnlySpan<char> message)
        {
            if (player is null
                || player.Arena is null
                || player.Status != PlayerState.Playing
                || message.Length > ChatPacket.MaxMessageChars)
            {
                return;
            }

            ReadOnlySpan<char> subtype = message.GetToken(":", out ReadOnlySpan<char> remaining);
            if (subtype.IsEmpty || remaining.IsEmpty)
                return;

            if (subtype.StartsWith("PUB", StringComparison.Ordinal) || subtype.Equals("CMD", StringComparison.Ordinal))
            {
                HandlePub(player, remaining[1..], subtype.Equals("PUBM", StringComparison.Ordinal), subtype.Equals("CMD", StringComparison.Ordinal), ChatSound.None);
            }
            else if (subtype.Equals("PRIV", StringComparison.Ordinal) || subtype.Equals("PRIVCMD", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> name = remaining.GetToken(':', out ReadOnlySpan<char> privRemaining);
                if (name.IsEmpty || privRemaining.IsEmpty)
                    return;

                Player? targetPlayer = _playerData.FindPlayer(name);
                if (targetPlayer is not null && targetPlayer.Arena == player.Arena)
                {
                    HandlePrivate(player, targetPlayer, privRemaining[1..], subtype.Equals("PRIVCMD", StringComparison.Ordinal), ChatSound.None);
                }
                else
                {
                    HandleRemotePrivate(player, remaining, subtype.Equals("PRIVCMD", StringComparison.Ordinal), ChatSound.None);
                }
            }
            else if (subtype.Equals("FREQ", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> freqStr = remaining.GetToken(':', out remaining);
                if (freqStr.IsEmpty || remaining.IsEmpty || !short.TryParse(freqStr, out short freq))
                    return;

                HandleFreq(player, freq, remaining[1..], ChatSound.None);
            }
            else if (subtype.Equals("CHAT", StringComparison.Ordinal))
            {
                HandleChat(player, remaining[1..], ChatSound.None);
            }
            else if (subtype.Equals("MOD", StringComparison.Ordinal))
            {
                HandleModChat(player, remaining[1..], ChatSound.None);
            }

            CheckFlood(player);
        }

        private void GetArenaSet(HashSet<Player> set, Arena? arena, Player? except)
        {
            ArgumentNullException.ThrowIfNull(set);

            _playerData.Lock();

            try
            {
                foreach (Player p in _playerData.Players)
                {
                    if (p.Status == PlayerState.Playing &&
                        (p.Arena == arena || arena is null) &&
                        p != except)
                    {
                        set.Add(p);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private void GetCapabilitySet(HashSet<Player> set, string capability, Player? except)
        {
            ArgumentNullException.ThrowIfNull(set);
            ArgumentException.ThrowIfNullOrWhiteSpace(capability);

            _playerData.Lock();

            try
            {
                foreach (Player p in _playerData.Players)
                {
                    if (p.Status == PlayerState.Playing
                        && _capabilityManager.HasCapability(p, capability)
                        && p != except)
                    {
                        set.Add(p);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private void CheckFlood(Player player)
        {
            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            lock (pd.Lock)
            {
                pd.MessageCount++;

                if (pd.MessageCount >= _cfg.FloodLimit
                    && _cfg.FloodLimit > 0
                    && !_capabilityManager.HasCapability(player, Constants.Capabilities.CanSpam))
                {
                    pd.MessageCount >>= 1;

                    if (pd.Expires is not null)
                    {
                        // already has a mask, add time
                        pd.Expires = pd.Expires.Value.AddSeconds(_cfg.FloodShutup);
                    }
                    else if (pd.Mask.IsClear)
                    {
                        // only set expiry time if this is a new shutup
                        pd.Expires = DateTime.UtcNow.AddSeconds(_cfg.FloodShutup);
                    }

                    pd.Mask.SetRestricted(ChatMessageType.PubMacro);
                    pd.Mask.SetRestricted(ChatMessageType.Pub);
                    pd.Mask.SetRestricted(ChatMessageType.Freq);
                    pd.Mask.SetRestricted(ChatMessageType.EnemyFreq);
                    pd.Mask.SetRestricted(ChatMessageType.Private);
                    pd.Mask.SetRestricted(ChatMessageType.RemotePrivate);
                    pd.Mask.SetRestricted(ChatMessageType.Chat);
                    pd.Mask.SetRestricted(ChatMessageType.ModChat);
                    pd.Mask.SetRestricted(ChatMessageType.BillerCommand);

                    ((IChat)this).SendMessage(player, $"You have been shut up for {_cfg.FloodShutup} seconds for flooding.");
                    _logManager.LogP(LogLevel.Info, nameof(Chat), player, $"Flooded chat, shut up for {_cfg.FloodShutup} seconds.");
                }
            }
        }

        private void SendMessage(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player? from, ReadOnlySpan<char> message)
        {
            if (type == ChatMessageType.ModChat)
                type = ChatMessageType.SysopWarning;

            if (_network is not null)
            {
                Span<byte> chatBytes = stackalloc byte[ChatPacket.GetPacketByteCount(message)];
                ref ChatPacket chatPacket = ref MemoryMarshal.AsRef<ChatPacket>(chatBytes);
                chatPacket.Type = (byte)S2CPacketType.Chat;
                chatPacket.ChatType = (byte)type;
                chatPacket.Sound = (byte)sound;
                chatPacket.PlayerId = from is not null ? (short)from.Id : (short)-1;
                int length = ChatPacket.SetMessage(chatBytes, message);

                _network.SendToSet(set, chatBytes[..length], NetSendFlags.Reliable);
            }

            string? ctype = GetChatType(type);
            if (_chatNetwork is not null && ctype is not null)
            {
                if (from is not null)
                {
                    _chatNetwork.SendToSet(set, $"MSG:{ctype}:{from.Name}:{message}");
                }
                else
                {
                    _chatNetwork.SendToSet(set, $"MSG:{ctype}:{message}");
                }
            }
        }

        private void HandleChat(Player player, ReadOnlySpan<char> text, ChatSound sound)
        {
            if (player is null)
                return;

            // Fix for broken clients that include an extra ; as the first character.
            // E.g., ;2;hello instead of 2;hello
            if (text.Length >= 1 && text[0] == ';')
                text = text[1..];

            if (Ok(player, ChatMessageType.Chat))
            {
                // msg should look like "text" or "#;text"
                FireChatMessageCallback(null, player, ChatMessageType.Chat, sound, null, -1, text);

#if CFG_LOG_PRIVATE
                _logManager.LogP(LogLevel.Drivel, "Chat", player, $"chat msg: {text}");
#endif
            }
        }

        private void HandleRemotePrivate(Player player, ReadOnlySpan<char> text, bool isAllCmd, ChatSound sound)
        {
            if (player is null)
                return;

            if (MemoryExtensions.IsWhiteSpace(text))
                return;

            if (text[0] != ':')
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Chat), player, "Malformed remote private message (must begin with a ':').");
                return;
            }

            ReadOnlySpan<char> dest = text.GetToken(':', out ReadOnlySpan<char> remaining);

            if (MemoryExtensions.IsWhiteSpace(dest))
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Chat), player, "Malformed remote private message (no destination).");
                return;
            }

            if (remaining.IsEmpty)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Chat), player, "Malformed remote private message (no ending ':' for destination).");
                return;
            }

            ReadOnlySpan<char> message = remaining[1..]; // remove the ':', everything after it is the message

            if ((message.Length > 1 && IsCommandChar(message[0])) || isAllCmd)
            {
                if (Ok(player, ChatMessageType.Command))
                {
                    Player? targetPlayer = _playerData.FindPlayer(dest);
                    if (targetPlayer is not null && targetPlayer.Status == PlayerState.Playing)
                    {
                        RunCommands(message, player, targetPlayer, sound);
                    }
                }
            }
            else if (Ok(player, ChatMessageType.RemotePrivate))
            {
                Player? destPlayer = _playerData.FindPlayer(dest);
                if (destPlayer is not null)
                {
                    if (destPlayer.Status != PlayerState.Playing)
                        return;

                    Span<char> messageToSend = stackalloc char[1 + player.Name!.Length + 3 + message.Length];
                    if (!messageToSend.TryWrite(CultureInfo.InvariantCulture, $"({player.Name})> {message}", out int _))
                    {
                        _logManager.LogP(LogLevel.Error, nameof(Chat), player, "Failed to write remote private message.");
                        return;
                    }

                    HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
                    try
                    {
                        set.Add(destPlayer);
                        SendReply(set, ChatMessageType.RemotePrivate, sound, player, -1, messageToSend, player.Name.Length + 4);
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(set);
                    }
                }

                // NOTE: The billing module looks for these if where destPlayer is null.
                FireChatMessageCallback(null, player, ChatMessageType.RemotePrivate, sound, destPlayer, -1, destPlayer is not null ? message : text);

#if CFG_LOG_PRIVATE
                _logManager.LogP(LogLevel.Drivel, "Chat", player, $"to [{dest}] remote priv: {message}");
#endif
            }
        }

        private void HandlePrivate(Player player, Player targetPlayer, ReadOnlySpan<char> text, bool isAllCmd, ChatSound sound)
        {
            if (player is null)
                return;

            Arena? arena = player.Arena; // this can be null

            if ((text.Length > 1 && IsCommandChar(text[0])) || isAllCmd)
            {
                if (Ok(player, ChatMessageType.Command))
                {
                    RunCommands(text, player, targetPlayer, sound);
                }
            }
            else if (Ok(player, ChatMessageType.Private))
            {
                HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    set.Add(targetPlayer);
                    SendReply(set, ChatMessageType.Private, sound, player, player.Id, text, 0);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(set);
                }

                FireChatMessageCallback(arena, player, ChatMessageType.Private, sound, targetPlayer, -1, text);

#if CFG_LOG_PRIVATE
                _logManager.LogP(LogLevel.Drivel, "Chat", player, $"to [{targetPlayer.Name}] priv msg: {text}");
#endif
            }
        }

        private void FireChatMessageCallback(Arena? arena, Player? playerFrom, ChatMessageType type, ChatSound sound, Player? playerTo, short freq, ReadOnlySpan<char> message)
        {
            // if we have an arena, then call the arena's callbacks, otherwise do the global ones
            ChatMessageCallback.Fire(arena ?? _broker, arena, playerFrom, type, sound, playerTo, freq, message);
        }

        private void HandleFreq(Player player, short freq, ReadOnlySpan<char> text, ChatSound sound)
        {
            if (player is null)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            ChatMessageType type = player.Freq == freq ? ChatMessageType.Freq : ChatMessageType.EnemyFreq;

            if (text.Length > 1 && IsCommandChar(text[0]))
            {
                if (Ok(player, ChatMessageType.Command))
                {
                    ITarget target = Target.TeamTarget(arena, freq);
                    RunCommands(text, player, target, sound);
                }
            }
            else if (Ok(player, type))
            {
                _playerData.Lock();

                try
                {
                    HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                    try
                    {
                        foreach (Player i in _playerData.Players)
                        {
                            if (i.Freq == freq
                                && i.Arena == arena
                                && i != player)
                            {
                                set.Add(i);
                            }
                        }

                        if (set.Count > 0)
                        {
                            SendReply(set, type, sound, player, player.Id, text, 0);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(set);
                    }

                    FireChatMessageCallback(arena, player, type, sound, null, freq, text);

                    _logManager.LogP(LogLevel.Drivel, nameof(Chat), player, $"freq msg ({freq}): {text}");
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
        }

        private static bool IsCommandChar(char c)
        {
            return c == CmdChar1 || c == CmdChar2;
        }

        private void HandleModChat(Player player, ReadOnlySpan<char> message, ChatSound sound)
        {
            if (_capabilityManager is null)
            {
                ((IChat)this).SendMessage(player, "Staff chat is currently disabled.");
                return;
            }

            if (_capabilityManager.HasCapability(player, Constants.Capabilities.SendModChat) && Ok(player, ChatMessageType.ModChat))
            {
                HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    GetCapabilitySet(set, Constants.Capabilities.ModChat, player);

                    if (set.Count > 0)
                    {
                        Span<char> messageToSend = stackalloc char[player.Name!.Length + 2 + message.Length];
                        if (!messageToSend.TryWrite(CultureInfo.InvariantCulture, $"{player.Name}> {message}", out int _))
                        {
                            _logManager.LogP(LogLevel.Error, nameof(Chat), player, "Failed to write mod chat message.");
                            return;
                        }

                        SendReply(set, ChatMessageType.ModChat, sound, player, player.Id, messageToSend, player.Name.Length + 2);
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(set);
                }

                FireChatMessageCallback(null, player, ChatMessageType.ModChat, sound, null, -1, message);

                _logManager.LogP(LogLevel.Drivel, nameof(Chat), player, $"mod chat: {message}");
            }
            else
            {
                ((IChat)this).SendMessage(player, "You aren't allowed to use the staff chat. If you need to send a message to the zone staff, use ?cheater.");

                _logManager.LogP(LogLevel.Drivel, nameof(Chat), player, $"Attempted mod chat (missing cap or shutup): {message}");
            }
        }

        private void HandlePub(Player player, ReadOnlySpan<char> msg, bool isMacro, bool isAllCmd, ChatSound sound)
        {
            if (player is null)
                return;

            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if ((msg.Length > 1 && IsCommandChar(msg[0])) || isAllCmd)
            {
                if (Ok(player, ChatMessageType.Command))
                {
                    RunCommands(msg, player, arena, sound);
                }
            }
            else
            {
                ChatMessageType type = isMacro ? ChatMessageType.PubMacro : ChatMessageType.Pub;
                if (Ok(player, type))
                {
                    HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                    try
                    {
                        GetArenaSet(set, arena, player);

                        if (set.Count > 0)
                        {
                            SendReply(set, type, sound, player, player.Id, msg, 0);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(set);
                    }

                    FireChatMessageCallback(arena, player, type, sound, null, -1, msg);

                    _logManager.LogP(LogLevel.Drivel, nameof(Chat), player, $"pub msg: {msg}");
                }
            }
        }

        private void RunCommands(ReadOnlySpan<char> msg, Player player, ITarget target, ChatSound sound)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(target);

            if (msg.Length < 2)
                return;

            if (MemoryExtensions.IsWhiteSpace(msg))
                return;

            char commandChar = '\0';

            // Skip initial ? or *
            if (IsCommandChar(msg[0]))
            {
                commandChar = msg[0];
                msg = msg[1..];

                if (MemoryExtensions.IsWhiteSpace(msg))
                    return;
            }

            if (msg[0] == MultiChar)
            {
                ReadOnlySpan<char> remaining = msg;
                int count = 0;

                while (!remaining.IsEmpty && count++ < _cfg.CommandLimit)
                {
                    ReadOnlySpan<char> token = StringUtils.GetToken(remaining, '|', out remaining);
                    if (token.IsWhiteSpace())
                        continue;

                    RunCommand(commandChar, token, player, target, sound);
                }
            }
            else
            {
                RunCommand(commandChar, msg, player, target, sound);
            }

            void RunCommand(char commandChar, scoped ReadOnlySpan<char> line, Player player, ITarget target, ChatSound sound)
            {
                Span<char> buffer = stackalloc char[ChatPacket.MaxMessageChars];

                // Give modules a chance to rewrite the command.
                var advisors = _broker.GetAdvisors<IChatAdvisor>();
                foreach (IChatAdvisor advisor in advisors)
                {
                    if (advisor.TryRewriteCommand(commandChar, line, buffer, out int charsWritten))
                    {
                        line = buffer[..charsWritten];
                        break;
                    }
                }

                // Run the command.
                _commandManager.Command(line, player, target, sound);
            }
        }

        private void SendReply(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player player, int fromPid, ReadOnlySpan<char> msg, int chatNetOffset)
        {
            string? ctype = GetChatType(type);

            NetSendFlags flags = NetSendFlags.None;
            if (type == ChatMessageType.PubMacro)
                flags |= NetSendFlags.PriorityN1;

            if (_cfg.MessageReliable)
                flags |= NetSendFlags.Reliable;

            if (type == ChatMessageType.ModChat)
                type = ChatMessageType.SysopWarning;

            Span<byte> chatBytes = stackalloc byte[ChatPacket.GetPacketByteCount(msg)];
            ref ChatPacket chatPacket = ref MemoryMarshal.AsRef<ChatPacket>(chatBytes);
            chatPacket.Type = (byte)S2CPacketType.Chat;
            chatPacket.ChatType = (byte)type;
            chatPacket.Sound = (byte)sound;
            chatPacket.PlayerId = (short)fromPid;

            HashSet<Player> filteredSet = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                if (_obscene is not null)
                {
                    foreach (Player otherPlayer in set)
                    {
                        if (otherPlayer.Flags.ObscenityFilter)
                            filteredSet.Add(otherPlayer);
                    }

                    set.ExceptWith(filteredSet);
                }

                if (set.Count > 0)
                {
                    if (_network is not null)
                    {
                        int length = ChatPacket.SetMessage(chatBytes, msg);
                        _network.SendToSet(set, chatBytes[..length], flags);
                    }

                    if (_chatNetwork is not null && ctype is not null)
                    {
                        _chatNetwork.SendToSet(set, $"MSG:{ctype}:{player.Name}:{msg[chatNetOffset..]}");
                    }
                }

                if (filteredSet.Count > 0)
                {
                    Span<char> filteredMsg = stackalloc char[msg.Length];
                    msg.CopyTo(filteredMsg);

                    bool replaced = _obscene!.Filter(filteredMsg);

                    if (!replaced || (replaced && _cfg.ObsceneFilterSendGarbageText))
                    {
                        if (_network is not null)
                        {
                            int length = ChatPacket.SetMessage(chatBytes, filteredMsg);
                            _network.SendToSet(filteredSet, chatBytes[..length], flags);
                        }

                        if (_chatNetwork is not null && ctype is not null)
                        {
                            _chatNetwork.SendToSet(filteredSet, $"MSG:{ctype}:{player.Name}:{filteredMsg[chatNetOffset..]}");
                        }
                    }
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(filteredSet);
            }
        }

        private static string? GetChatType(ChatMessageType type) => type switch
        {
            ChatMessageType.Arena => "ARENA",
            ChatMessageType.PubMacro => "PUBM",
            ChatMessageType.Pub => "PUB",
            ChatMessageType.Freq => "FREQ",
            ChatMessageType.EnemyFreq => "FREQ",
            ChatMessageType.Private => "PRIV",
            ChatMessageType.RemotePrivate => "PRIV",
            ChatMessageType.SysopWarning => "SYSOP",
            ChatMessageType.Chat => "CHAT",
            ChatMessageType.ModChat => "MOD",
            _ => null,
        };

        private bool Ok(Player player, ChatMessageType messageType)
        {
            if (player is null)
                return false;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return false;

            ArenaData? ad = null;
            player.Arena?.TryGetExtraData(_adKey, out ad);

            ChatMask mask;

            lock (pd.Lock)
            {
                ExpireMask(player);

                mask = pd.Mask;

                if (ad is not null)
                {
                    ad.Lock.EnterReadLock();

                    try
                    {
                        mask |= ad.Mask;
                    }
                    finally
                    {
                        ad.Lock.ExitReadLock();
                    }
                }
            }

            return mask.IsAllowed(messageType);
        }

        private void ExpireMask(Player player)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                return;

            DateTime now = DateTime.UtcNow;

            lock (pd.Lock)
            {
                // handle expiring masks
                if (pd.Expires is not null
                    && now > pd.Expires)
                {
                    pd.Mask.Clear();
                    pd.Expires = null;
                }

                // handle exponential decay of msg count
                pd.MessageCount >>= Math.Clamp((int)(now - pd.LastCheck).TotalSeconds, 0, 31);
                pd.LastCheck = now;
            }
        }

        #region Helper types

        private readonly struct Config
        {
            /// <summary>
            /// Whether to send chat messages reliably.
            /// </summary>
            [ConfigHelp<bool>("Chat", "MessageReliable", ConfigScope.Global, Default = true,
                Description = "Whether to send chat messages reliably.")]
            public readonly bool MessageReliable;

            /// <summary>
            /// How many messages needed to be sent in a short period of time (about a second) to qualify for chat flooding.
            /// </summary>
            [ConfigHelp<int>("Chat", "FloodLimit", ConfigScope.Global, Default = 10,
                Description = "How many messages needed to be sent in a short period of time (about a second) to qualify for chat flooding.")]
            public readonly int FloodLimit;

            /// <summary>
            /// How many seconds to disable chat for a player that is flooding chat messages.
            /// </summary>
            [ConfigHelp<int>("Chat", "FloodShutup", ConfigScope.Global, Default = 60,
                Description = "How many seconds to disable chat for a player that is flooding chat messages.")]
            public readonly int FloodShutup;

            /// <summary>
            /// How many commands are allowed on a single line.
            /// </summary>
            [ConfigHelp<int>("Chat", "CommandLimit", ConfigScope.Global, Default = 5,
                Description = "How many commands are allowed on a single line.")]
            public readonly int CommandLimit;

            /// <summary>
            /// If true, replace obscene words with garbage characters, otherwise suppress whole line.
            /// </summary>
            [ConfigHelp<bool>("Chat", "FilterMode", ConfigScope.Global, Default = true,
                Description = "If true, replace obscene words with garbage characters, otherwise suppress whole line.")]
            public readonly bool ObsceneFilterSendGarbageText;

            public Config(IConfigManager configManager)
            {
                MessageReliable = configManager.GetBool(configManager.Global, "Chat", "MessageReliable", GlobalChatSettings.MessageReliable.Default);
                FloodLimit = configManager.GetInt(configManager.Global, "Chat", "FloodLimit", GlobalChatSettings.FloodLimit.Default);
                FloodShutup = configManager.GetInt(configManager.Global, "Chat", "FloodShutup", GlobalChatSettings.FloodShutup.Default);
                CommandLimit = configManager.GetInt(configManager.Global, "Chat", "CommandLimit", GlobalChatSettings.CommandLimit.Default);
                ObsceneFilterSendGarbageText = configManager.GetBool(configManager.Global, "Chat", "FilterMode", GlobalChatSettings.FilterMode.Default);
            }
        }

        private class ArenaData : IResettable
        {
            /// <summary>
            /// The arena's chat mask.
            /// </summary>
            public ChatMask Mask;

            public readonly ReaderWriterLockSlim Lock = new();

            public bool TryReset()
            {
                Lock.EnterWriteLock();

                try
                {
                    Mask = new();
                }
                finally
                {
                    Lock.ExitWriteLock();
                }

                return true;
            }
        }

        private class PlayerData : IResettable
        {
            /// <summary>
            /// The player's chat mask.
            /// </summary>
            public ChatMask Mask;

            /// <summary>
            /// When the mask expires.
            /// <see langword="null"/> for a session long mask.
            /// </summary>
            public DateTime? Expires;

            /// <summary>
            /// A count of messages. This decays exponentially 50% per second.
            /// </summary>
            public int MessageCount;

            /// <summary>
            /// When the <see cref="MessageCount"/> was last checked. Used to decay the count.
            /// </summary>
            public DateTime LastCheck;

            public readonly Lock Lock = new();

            public void Reset()
            {
                lock (Lock)
                {
                    Mask.Clear();
                    Expires = null;
                    MessageCount = 0;
                    LastCheck = DateTime.UtcNow;
                }
            }

            bool IResettable.TryReset()
            {
                Reset();
                return true;
            }
        }

        #endregion
    }
}

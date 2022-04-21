using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.ObjectPool;
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
    public class Chat : IModule, IChat
    {
        private const char CmdChar1 = '?';
        private const char CmdChar2 = '*';
        private const char MultiChar = '|';
        private const char ModChatChar = '\\';

        private ComponentBroker _broker;
       
        // required dependencies
        private IArenaManager _arenaManager;
        private ICapabilityManager _capabilityManager;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;

        // optional dependencies
        private IChatNet _chatNet;
        private INetwork _network;
        private IObscene _obscene;
        private IPersist _persist;

        private InterfaceRegistrationToken<IChat> _iChatToken;

        private readonly struct Config
        {
            /// <summary>
            /// Whether to send chat messages reliably.
            /// </summary>
            [ConfigHelp("Chat", "MessageReliable", ConfigScope.Global, typeof(bool), DefaultValue = "1", 
                Description = "Whether to send chat messages reliably.")]
            public readonly bool MessageReliable;

            /// <summary>
            /// How many messages needed to be sent in a short period of time (about a second) to qualify for chat flooding.
            /// </summary>
            [ConfigHelp("Chat", "FloodLimit", ConfigScope.Global, typeof(int), DefaultValue = "10", 
                Description = "How many messages needed to be sent in a short period of time (about a second) to qualify for chat flooding.")]
            public readonly int FloodLimit;

            /// <summary>
            /// How many seconds to disable chat for a player that is flooding chat messages.
            /// </summary>
            [ConfigHelp("Chat", "FloodShutup", ConfigScope.Global, typeof(int), DefaultValue = "60", 
                Description = "How many seconds to disable chat for a player that is flooding chat messages.")]
            public readonly int FloodShutup;

            /// <summary>
            /// How many commands are allowed on a single line.
            /// </summary>
            [ConfigHelp("Chat", "CommandLimit", ConfigScope.Global, typeof(int), DefaultValue = "5", 
                Description = "How many commands are allowed on a single line.")]
            public readonly int CommandLimit;

            /// <summary>
            /// If true, replace obscene words with garbage characters, otherwise suppress whole line.
            /// </summary>
            [ConfigHelp("Chat", "FilterMode", ConfigScope.Global, typeof(bool), DefaultValue = "1",
                Description = "If true, replace obscene words with garbage characters, otherwise suppress whole line.")]
            public readonly bool FilterSendGarbageText;

            public Config(IConfigManager configManager)
            {
                MessageReliable = configManager.GetInt(configManager.Global, "Chat", "MessageReliable", 1) != 0;
                FloodLimit = configManager.GetInt(configManager.Global, "Chat", "FloodLimit", 10);
                FloodShutup = configManager.GetInt(configManager.Global, "Chat", "FloodShutup", 60);
                CommandLimit = configManager.GetInt(configManager.Global, "Chat", "CommandLimit", 5);
                FilterSendGarbageText = configManager.GetInt(configManager.Global, "Chat", "FilterMode", 1) != 0;
            }
        }

        private Config _cfg;

        private ArenaDataKey<ArenaData> _adKey;
        private PlayerDataKey<PlayerData> _pdKey;

        private class ArenaData
        {
            /// <summary>
            /// The arena's chat mask.
            /// </summary>
            public ChatMask Mask;

            public readonly ReaderWriterLockSlim Lock = new();
        }

        private class PlayerData
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

            public readonly object Lock = new();

            public void Clear()
            {
                lock (Lock)
                {
                    Mask.Clear();
                    Expires = null;
                    MessageCount = 0;
                    LastCheck = DateTime.UtcNow;
                }
            }
        }

        private DelegatePersistentData<Player> _persistRegistration;

        #region Module Members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            ICapabilityManager capabilityManager,
            ICommandManager commandManager,
            IConfigManager configManager,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(broker));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _network = broker.GetInterface<INetwork>();
            _chatNet = broker.GetInterface<IChatNet>();

            if (_network == null && _chatNet == null)
            {
                // need at least one of the network interfaces
                _logManager.LogM(LogLevel.Error, nameof(Chat), "Failed to get at least one of the network interfaces (INetwork or IChatNet).");
                return false;
            }

            _persist = broker.GetInterface<IPersist>();
            _obscene = broker.GetInterface<IObscene>();

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            if (_persist != null)
            {
                _persistRegistration = new DelegatePersistentData<Player>(
                    (int)PersistKey.Chat, PersistInterval.ForeverNotShared, PersistScope.PerArena, Persist_GetData, Persist_SetData, Persist_ClearData);
                _persist.RegisterPersistentData(_persistRegistration);
            }

            _cfg = new Config(_configManager);

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            PlayerActionCallback.Register(_broker, Callback_PlayerAction);

            _network?.AddPacket(C2SPacketType.Chat, Packet_Chat);
            _chatNet?.AddHandler("SEND", ChatNet_Chat);

            _iChatToken = _broker.RegisterInterface<IChat>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iChatToken) != 0)
                return false;

            _network?.RemovePacket(C2SPacketType.Chat, Packet_Chat);
            _chatNet?.RemoveHandler("SEND", ChatNet_Chat);

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);

            if (_persist != null && _persistRegistration != null)
                _persist.UnregisterPersistentData(_persistRegistration);

            _arenaManager.FreeArenaData(_adKey);
            _playerData.FreePlayerData(_pdKey);

            if (_persist != null)
                _broker.ReleaseInterface(ref _persist);

            if (_obscene != null)
                _broker.ReleaseInterface(ref _obscene);

            if (_network != null)
                _broker.ReleaseInterface(ref _network);

            if (_chatNet != null)
                _broker.ReleaseInterface(ref _chatNet);

            return true;
        }

        #endregion

        #region IChat Members

        void IChat.SendMessage(Player p, ref ChatSendMessageInterpolatedStringHandler handler)
        {
            try
            {
                ((IChat)this).SendMessage(p, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChat.SendMessage(Player p, ReadOnlySpan<char> message)
        {
            SendMessage(p, message);
        }

        void IChat.SendMessage(Player p, string message)
        {
            ((IChat)this).SendMessage(p, message.AsSpan());
        }

        void IChat.SendMessage(Player p, StringBuilder message)
        {
            SendMessage(p, message);
        }

        void IChat.SendMessage(Player p, ChatSound sound, ref ChatSendMessageInterpolatedStringHandler handler)
        {
            try
            {
                ((IChat)this).SendMessage(p, sound, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChat.SendMessage(Player p, ChatSound sound, ReadOnlySpan<char> message)
        {
            SendMessage(p, ChatMessageType.Arena, sound, null, message);
        }

        void IChat.SendMessage(Player p, ChatSound sound, string message)
        {
            ((IChat)this).SendMessage(p, sound, message.AsSpan());
        }

        void IChat.SendMessage(Player p, ChatSound sound, StringBuilder message)
        {
            Span<char> text = stackalloc char[Math.Min(ChatPacket.MaxMessageChars, message.Length)];
            message.CopyTo(0, text, text.Length);
            SendMessage(p, ChatMessageType.Arena, sound, null, text);
        }

        void IChat.SendSetMessage(HashSet<Player> set, ref ChatSendMessageInterpolatedStringHandler handler)
        {
            try
            {
                ((IChat)this).SendSetMessage(set, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChat.SendSetMessage(HashSet<Player> set, ReadOnlySpan<char> message)
        {
            SendMessage(set, ChatMessageType.Arena, ChatSound.None, null, message);
        }

        void IChat.SendSetMessage(HashSet<Player> set, string message)
        {
            ((IChat)this).SendSetMessage(set, message.AsSpan());
        }

        void IChat.SendSetMessage(HashSet<Player> set, StringBuilder message)
        {
            Span<char> text = stackalloc char[Math.Min(ChatPacket.MaxMessageChars, message.Length)];
            message.CopyTo(0, text, text.Length);
            SendMessage(set, ChatMessageType.Arena, ChatSound.None, null, text);
        }

        void IChat.SendSetMessage(HashSet<Player> set, ChatSound sound, ref ChatSendMessageInterpolatedStringHandler handler)
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
            SendMessage(set, ChatMessageType.Arena, sound, null, text);
        }

        void IChat.SendArenaMessage(Arena arena, ref ChatSendMessageInterpolatedStringHandler handler)
        {
            try
            {
                ((IChat)this).SendArenaMessage(arena, handler.StringBuilder);
            }
            finally
            {
                handler.Clear();
            }
        }

        void IChat.SendArenaMessage(Arena arena, ReadOnlySpan<char> message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetArenaSet(set, arena, null);

                if (set.Count > 0)
                    SendMessage(set, ChatMessageType.Arena, ChatSound.None, null, message);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        void IChat.SendArenaMessage(Arena arena, string message)
        {
            ((IChat)this).SendArenaMessage(arena, message.AsSpan());
        }

        void IChat.SendArenaMessage(Arena arena, StringBuilder message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetArenaSet(set, arena, null);

                if (set.Count > 0)
                {
                    Span<char> text = stackalloc char[Math.Min(ChatPacket.MaxMessageChars, message.Length)];
                    message.CopyTo(0, text, text.Length);
                    SendMessage(set, ChatMessageType.Arena, ChatSound.None, null, text);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        void IChat.SendArenaMessage(Arena arena, ChatSound sound, ref ChatSendMessageInterpolatedStringHandler handler)
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

        void IChat.SendArenaMessage(Arena arena, ChatSound sound, ReadOnlySpan<char> message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetArenaSet(set, arena, null);

                if (set.Count > 0)
                    SendMessage(set, ChatMessageType.Arena, sound, null, message);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        void IChat.SendArenaMessage(Arena arena, ChatSound sound, string message)
        {
            ((IChat)this).SendArenaMessage(arena, sound, message.AsSpan());
        }

        void IChat.SendArenaMessage(Arena arena, ChatSound sound, StringBuilder message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                GetArenaSet(set, arena, null);

                if (set.Count > 0)
                {
                    Span<char> text = stackalloc char[Math.Min(ChatPacket.MaxMessageChars, message.Length)];
                    message.CopyTo(0, text, text.Length);
                    SendMessage(set, ChatMessageType.Arena, sound, null, text);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        void IChat.SendAnyMessage(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player from, ref ChatSendMessageInterpolatedStringHandler handler)
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

        void IChat.SendAnyMessage(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player from, ReadOnlySpan<char> message)
        {
            SendMessage(set, type, sound, from, message);
        }

        void IChat.SendAnyMessage(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player from, string message)
        {
            ((IChat)this).SendAnyMessage(set, type, sound, from, message.AsSpan());
        }

        void IChat.SendAnyMessage(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player from, StringBuilder message)
        {
            Span<char> text = stackalloc char[Math.Min(ChatPacket.MaxMessageChars, message.Length)];
            message.CopyTo(0, text, text.Length);
            SendMessage(set, type, sound, from, text);
        }

        void IChat.SendModMessage(ref ChatSendMessageInterpolatedStringHandler handler)
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
            if (_network != null)
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

                    ChatPacket cp = new();
                    cp.Type = (byte)S2CPacketType.Chat;
                    cp.ChatType = (byte)ChatMessageType.RemotePrivate;
                    cp.Sound = (byte)sound;
                    cp.PlayerId = -1;
                    int length = ChatPacket.LengthWithoutMessage + cp.SetMessage(text);

                    _network.SendToSet(
                        set,
                        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref cp, 1)).Slice(0, length),
                        NetSendFlags.Reliable);
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }

            if (_chatNet != null)
            {
                // TODO: chatnet
                //_chatNet.SendToSet(
                //    set,
                //    )
            }
        }

        ChatMask IChat.GetArenaChatMask(Arena arena)
        {
            if(arena == null)
                return new ChatMask();

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
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
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
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

        ChatMask IChat.GetPlayerChatMask(Player p)
        {
            if (p == null)
                return new ChatMask();

            if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                return new ChatMask();

            lock (pd.Lock)
            {
                ExpireMask(p);
                return pd.Mask;
            }
        }

        void IChat.GetPlayerChatMask(Player p, out ChatMask mask, out TimeSpan? remaining)
        {
            if (p == null
                || !p.TryGetExtraData(_pdKey, out PlayerData pd))
            {
                mask = default;
                remaining = default;
                return;
            }

            lock (pd.Lock)
            {
                DateTime now = DateTime.UtcNow;

                ExpireMask(p);
                mask = pd.Mask;

                remaining = pd.Expires == null ? new TimeSpan?() : now - pd.Expires.Value;
            }
        }

        void IChat.SetPlayerChatMask(Player p, ChatMask mask, int timeout)
        {
            if (p == null)
                return;

            if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            lock (pd.Lock)
            {
                pd.Mask = mask;

                if (timeout == 0)
                    pd.Expires = null;
                else
                    pd.Expires = DateTime.UtcNow.AddSeconds(timeout);
            }
        }

        void IChat.SendWrappedText(Player p, string text)
        {
            if (p == null)
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

                    SendMessage(p, sb);
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        void IChat.SendWrappedText(Player p, StringBuilder sb)
        {
            if (p == null)
                return;

            if (sb == null || sb.Length == 0)
                return;

            StringBuilder tempBuilder = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                foreach (ReadOnlySpan<char> line in sb.GetWrappedText(78, ' ')) // foreach handles calling Dispose() on the enumerator
                {
                    tempBuilder.Clear();
                    tempBuilder.Append("  ");
                    tempBuilder.Append(line);

                    SendMessage(p, tempBuilder);
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(tempBuilder);
            }
        }

        ObjectPool<StringBuilder> IChat.StringBuilderPool => _objectPoolManager.StringBuilderPool;

        #endregion

        #region Persist methods

        private void Persist_GetData(Player player, Stream outStream)
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            SSProto.ChatMask protoChatMask;

            lock (pd.Lock)
            {
                if (pd.Expires == null)
                    return;

                protoChatMask = new();
                protoChatMask.Mask = pd.Mask.Value;
                protoChatMask.Expires = pd.Expires != null ? Timestamp.FromDateTime(pd.Expires.Value) : null;
                protoChatMask.MessageCount = pd.MessageCount;
                protoChatMask.LastCheck = Timestamp.FromDateTime(pd.LastCheck);
            }

            protoChatMask.WriteTo(outStream);
        }

        private void Persist_SetData(Player player, Stream inStream)
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData pd))
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

        private void Persist_ClearData(Player player)
        {
            if (player == null || !player.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            lock (pd.Lock)
            {
                pd.Clear();
            }
        }

        #endregion

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null)
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                // TODO: settings for mask
            }
        }

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (p == null)
                return;

            if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            if (action == PlayerAction.PreEnterArena)
            {
                pd.Clear();
            }
        }

        private void Packet_Chat(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len < ChatPacket.MinLength
                || len > ChatPacket.MaxLength) // Note: for some reason ASSS checks if > 500 instead
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, $"Bad chat packet (length={len}).");
                return;
            }

            Arena arena = p.Arena;
            if (arena == null || p.Status != PlayerState.Playing)
                return;

            ref ChatPacket from = ref MemoryMarshal.AsRef<ChatPacket>(data);

            // Determine which bytes are part of the message.
            Span<byte> messageBytes = from.GetMessageBytes(len);
            messageBytes = messageBytes.SliceNullTerminated();

            // Decode the bytes.
            Span<char> text = stackalloc char[StringUtils.DefaultEncoding.GetCharCount(messageBytes)];
            StringUtils.DefaultEncoding.GetChars(messageBytes, text);

            // Remove control characters from the chat message.
            RemoveControlCharacters(text);

            ChatSound sound = _capabilityManager.HasCapability(p, Constants.Capabilities.SoundMessages)
                ? (ChatSound)from.Sound
                : ChatSound.None;

            Player target;
            switch ((ChatMessageType)from.ChatType)
            {
                case ChatMessageType.Arena:
                    _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "Received arena message.");
                    break;

                case ChatMessageType.PubMacro:
                case ChatMessageType.Pub:
                    if (text[0] == ModChatChar)
                        HandleModChat(p, text[1..], sound);
                    else
                        HandlePub(p, text, from.ChatType == (byte)ChatMessageType.PubMacro, false, sound);
                    break;

                case ChatMessageType.EnemyFreq:
                    target = _playerData.PidToPlayer(from.PlayerId);
                    if (target == null)
                        break;

                    if (target.Arena == arena)
                        HandleFreq(p, target.Freq, text, sound);
                    else
                        _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "Received cross-arena enemy freq chat message.");
                    break;

                case ChatMessageType.Freq:
                    HandleFreq(p, p.Freq, text, sound);
                    break;

                case ChatMessageType.Private:
                    target = _playerData.PidToPlayer(from.PlayerId);
                    if (target == null)
                        break;

                    if (target.Arena == arena)
                        HandlePrivate(p, target, text, false, sound);
                    else
                        _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "Received cross-arena private chat message.");
                    break;

                case ChatMessageType.RemotePrivate:
                    HandleRemotePrivate(p, text, false, sound);
                    break;

                case ChatMessageType.SysopWarning:
                    _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "Received sysop message.");
                    break;

                case ChatMessageType.Chat:
                    HandleChat(p, text, sound);
                    break;

                default:
                    _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, $"Received undefined chat message type {from.ChatType}.");
                    break;
            }

            CheckFlood(p);
        }

        private void ChatNet_Chat(Player p, ReadOnlySpan<char> message)
        {
            // TODO: chatnet
        }

        private void GetArenaSet(HashSet<Player> set, Arena arena, Player except)
        {
            if (set == null)
                throw new ArgumentNullException(nameof(set));

            _playerData.Lock();

            try
            {
                foreach (Player p in _playerData.Players)
                {
                    if (p.Status == PlayerState.Playing &&
                        (p.Arena == arena || arena == null) &&
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

        private void GetCapabilitySet(HashSet<Player> set, string capability, Player except)
        {
            if (set == null)
                throw new ArgumentNullException(nameof(set));

            if (string.IsNullOrWhiteSpace(capability))
                throw new ArgumentException("Cannot be null or white-space.", nameof(capability));

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

        private static void RemoveControlCharacters(Span<char> characters)
        {
            for (int i = 0; i < characters.Length ; i++)
            {
                if (char.IsControl(characters[i]))
                {
                    characters[i] = '_';
                }
            }
        }

        private void CheckFlood(Player p)
        {
            if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            lock (pd.Lock)
            {
                pd.MessageCount++;

                if (pd.MessageCount >= _cfg.FloodLimit 
                    &&  _cfg.FloodLimit > 0 
                    && !_capabilityManager.HasCapability(p, Constants.Capabilities.CanSpam))
                {
                    pd.MessageCount >>= 1;

                    if (pd.Expires != null)
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

                    SendMessage(p, $"You have been shut up for {_cfg.FloodShutup} seconds for flooding.");
                    _logManager.LogP(LogLevel.Info, nameof(Chat), p, $"Flooded chat, shut up for {_cfg.FloodShutup} seconds.");
                }
            }
        }

        private void SendMessage(Player p, ReadOnlySpan<char> message)
        {
            SendMessage(p, ChatMessageType.Arena, ChatSound.None, null, message);
        }

        private void SendMessage(Player p, StringBuilder message)
        {
            Span<char> messageSpan = stackalloc char[Math.Min(ChatPacket.MaxMessageChars, message.Length)];
            message.CopyTo(0, messageSpan, messageSpan.Length);
            SendMessage(p, ChatMessageType.Arena, ChatSound.None, null, messageSpan);
        }

        private void SendMessage(Player p, ChatMessageType type, ChatSound sound, Player from, ReadOnlySpan<char> message)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                set.Add(p);
                SendMessage(set, type, sound, from, message);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        private void SendMessage(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player from, ReadOnlySpan<char> message)
        {
            if (type == ChatMessageType.ModChat)
                type = ChatMessageType.SysopWarning;

            if (_network != null)
            {
                ChatPacket cp = new();
                cp.Type = (byte)S2CPacketType.Chat;
                cp.ChatType = (byte)type;
                cp.Sound = (byte)sound;
                cp.PlayerId = from != null ? (short)from.Id : (short)-1;
                int length = ChatPacket.LengthWithoutMessage + cp.SetMessage(message);

                _network.SendToSet(
                    set,
                    MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref cp, 1)).Slice(0, length),
                    NetSendFlags.Reliable);
            }

            string ctype = GetChatType(type);
            if (_chatNet != null && ctype != null)
            {
                // TODO: chatnet
                //if(from != null)
            }
        }

        private void HandleChat(Player p, Span<char> text, ChatSound sound)
        {
            if (p == null)
                return;

            // Fix for broken clients that include an extra ; as the first character.
            // E.g., ;2;hello instead of 2;hello
            if (text.Length >= 1 && text[0] == ';')
                text = text[1..];

            if (Ok(p, ChatMessageType.Chat))
            {
                // msg should look like "text" or "#;text"
                FireChatMessageCallback(null, p, ChatMessageType.Chat, sound, null, -1, text);

#if CFG_LOG_PRIVATE
                _logManager.LogP(LogLevel.Drivel, "Chat", p, $"chat msg: {text}");
#endif
            }
        }

        private void HandleRemotePrivate(Player p, Span<char> text, bool isAllCmd, ChatSound sound)
        {
            if (p == null)
                return;

            if (MemoryExtensions.IsWhiteSpace(text))
                return;

            if (text[0] != ':')
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "Malformed remote private message (must begin with a ':').");
                return;
            }

            Span<char> dest = text.GetToken(':', out Span<char> remaining);

            if (MemoryExtensions.IsWhiteSpace(dest))
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "Malformed remote private message (no destination).");
                return;
            }

            if (remaining.IsEmpty)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "Malformed remote private message (no ending ':' for destination).");
                return;
            }

            Span<char> message = remaining[1..]; // remove the ':', everything after it is the message

            if ((message.Length > 1 && IsCommandChar(message[0])) || isAllCmd)
            {
                if (Ok(p, ChatMessageType.Command))
                {
                    Player d = _playerData.FindPlayer(dest);
                    if (d != null && d.Status == PlayerState.Playing)
                    {
                        RunCommands(message, p, d, sound);
                    }
                }
            }
            else if (Ok(p, ChatMessageType.RemotePrivate))
            {
                Player d = _playerData.FindPlayer(dest);
                if (d != null)
                {
                    if (d.Status != PlayerState.Playing)
                        return;

                    Span<char> messageToSend = stackalloc char[1 + p.Name.Length + 3 + message.Length];
                    if (!messageToSend.TryWrite(CultureInfo.InvariantCulture, $"({p.Name})> {message}", out int _))
                    {
                        _logManager.LogP(LogLevel.Error, nameof(Chat), p, $"Failed to write remote private message.");
                        return;
                    }

                    HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();
                    try
                    {
                        set.Add(d);
                        SendReply(set, ChatMessageType.RemotePrivate, sound, p, -1, messageToSend, p.Name.Length + 3);
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(set);
                    }
                }

                FireChatMessageCallback(null, p, ChatMessageType.RemotePrivate, sound, d, -1, d != null ? message : text);

#if CFG_LOG_PRIVATE
                _logManager.LogP(LogLevel.Drivel, "Chat", p, $"to [{dest}] remote priv: {message}");
#endif
            }
        }

        private void HandlePrivate(Player p, Player dst, Span<char> text, bool isAllCmd, ChatSound sound)
        {
            if (p == null)
                return;

            if (MemoryExtensions.IsWhiteSpace(text))
                return;

            Arena arena = p.Arena; // this can be null

            if ((IsCommandChar(text[0]) && text.Length > 1) || isAllCmd)
            {
                if (Ok(p, ChatMessageType.Command))
                {
                    RunCommands(text, p, dst, sound);
                }
            }
            else if (Ok(p, ChatMessageType.Private))
            {
                HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    set.Add(dst);
                    SendReply(set, ChatMessageType.Private, sound, p, p.Id, text, 0);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(set);
                }

                FireChatMessageCallback(arena, p, ChatMessageType.Private, sound, null, -1, text);

#if CFG_LOG_PRIVATE
                _logManager.LogP(LogLevel.Drivel, "Chat", p, $"to [{dst.Name}] priv msg: {text}");
#endif
            }
        }

        private void FireChatMessageCallback(Arena arena, Player playerFrom, ChatMessageType type, ChatSound sound, Player playerTo, short freq, ReadOnlySpan<char> message)
        {
            // if we have an arena, then call the arena's callbacks, otherwise do the global ones
            if (arena != null)
                ChatMessageCallback.Fire(arena, playerFrom, type, sound, playerTo, freq, message);
            else
                ChatMessageCallback.Fire(_broker, playerFrom, type, sound, playerTo, freq, message);
        }

        private void HandleFreq(Player p, short freq, Span<char> text, ChatSound sound)
        {
            if (p == null)
                return;

            if (MemoryExtensions.IsWhiteSpace(text))
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            ChatMessageType type = p.Freq == freq ? ChatMessageType.Freq : ChatMessageType.EnemyFreq;

            if (IsCommandChar(text[0]) && text.Length > 1)
            {
                if (Ok(p, ChatMessageType.Command))
                {
                    ITarget target = Target.TeamTarget(p.Arena, p.Freq);
                    RunCommands(text, p, target, sound);
                }
            }
            else if(Ok(p, type))
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
                                && i != p)
                            {
                                set.Add(i);
                            }
                        }

                        if (set.Count > 0)
                        {
                            SendReply(set, type, sound, p, p.Id, text, 0);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(set);
                    }

                    FireChatMessageCallback(arena, p, type, sound, null, freq, text);

                    _logManager.LogP(LogLevel.Drivel, nameof(Chat), p, $"freq msg ({freq}): {text}");
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

        private void HandleModChat(Player p, Span<char> message, ChatSound sound)
        {
            if (_capabilityManager == null)
            {
                SendMessage(p, "Staff chat is currently disabled");
                return;
            }

            if (_capabilityManager.HasCapability(p, Constants.Capabilities.SendModChat) && Ok(p, ChatMessageType.ModChat))
            {
                HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    GetCapabilitySet(set, Constants.Capabilities.ModChat, p);

                    if (set.Count > 0)
                    {
                        Span<char> messageToSend = stackalloc char[p.Name.Length + 2 + message.Length];
                        if (!messageToSend.TryWrite(CultureInfo.InvariantCulture, $"{p.Name}> {message}", out int _))
                        {
                            _logManager.LogP(LogLevel.Error, nameof(Chat), p, $"Failed to write mod chat message.");
                            return;
                        }

                        SendReply(set, ChatMessageType.ModChat, sound, p, p.Id, messageToSend, p.Name.Length + 2);
                    }
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(set);
                }

                FireChatMessageCallback(null, p, ChatMessageType.ModChat, sound, null, -1, message);

                _logManager.LogP(LogLevel.Drivel, nameof(Chat), p, $"mod chat: {message}");
            }
            else
            {
                SendMessage(p, "You aren't allowed to use the staff chat. If you need to send a message to the zone staff, use ?cheater.");

                _logManager.LogP(LogLevel.Drivel, nameof(Chat), p, $"Attempted mod chat (missing cap or shutup): {message}");
            }
        }

        private void HandlePub(Player p, Span<char> msg, bool isMacro, bool isAllCmd, ChatSound sound)
        {
            if (p == null)
                return;

            if (MemoryExtensions.IsWhiteSpace(msg))
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            if ((IsCommandChar(msg[0]) && (msg.Length > 1)) || isAllCmd)
            {
                if (Ok(p, ChatMessageType.Command))
                {
                    RunCommands(msg, p, arena, sound);
                }
            }
            else
            {
                ChatMessageType type = isMacro ? ChatMessageType.PubMacro : ChatMessageType.Pub;
                if (Ok(p, type))
                {
                    HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                    try
                    {
                        GetArenaSet(set, arena, p);

                        if (set.Count > 0)
                        {
                            SendReply(set, type, sound, p, p.Id, msg, 0);
                        }
                    }
                    finally
                    {
                        _objectPoolManager.PlayerSetPool.Return(set);
                    }

                    FireChatMessageCallback(arena, p, type, sound, null, -1, msg);

                    _logManager.LogP(LogLevel.Drivel, nameof(Chat), p, $"pub msg: {msg}");
                }
            }
        }

        private void RunCommands(Span<char> msg, Player p, ITarget target, ChatSound sound)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));

            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (MemoryExtensions.IsWhiteSpace(msg))
                return;

            // skip initial ? or *
            if (IsCommandChar(msg[0]))
            {
                msg = msg[1..];

                if (MemoryExtensions.IsWhiteSpace(msg))
                    return;
            }

            bool multi = msg[0] == MultiChar;

            if (multi)
            {
                ReadOnlySpan<char> remaining = msg;
                int count = 0;
                while (++count < _cfg.CommandLimit && !remaining.IsEmpty)
                {
                    ReadOnlySpan<char> token = StringUtils.GetToken(msg, '|', out remaining);
                    if (token.IsWhiteSpace())
                        continue;

                    // TODO: give modules a chance to rewrite the command

                    // run the command
                    _commandManager.Command(token.ToString(), p, target, sound); // TODO: change CommandManager and all command delegates to accept ReadOnlySpan<char>
                }
            }
            else
            {
                // TODO: give modules a chance to rewrite the command

                // run the command
                _commandManager.Command(msg.ToString(), p, target, sound); // TODO: change CommandManager and all command delegates to accept ReadOnlySpan<char>
            }
        }

        private void SendReply(HashSet<Player> set, ChatMessageType type, ChatSound sound, Player p, int fromPid, Span<char> msg, int chatNetOffset)
        {
            //string ctype = GetChatType(type);

            NetSendFlags flags = NetSendFlags.None;
            if (type == ChatMessageType.PubMacro)
                flags |= NetSendFlags.PriorityN1;

            if (_cfg.MessageReliable)
                flags |= NetSendFlags.Reliable;

            if (type == ChatMessageType.ModChat)
                type = ChatMessageType.SysopWarning;

            ChatPacket to = new();
            to.Type = (byte)S2CPacketType.Chat;
            to.ChatType = (byte)type;
            to.Sound = (byte)sound;
            to.PlayerId = (short)fromPid;
            int length = ChatPacket.LengthWithoutMessage + to.SetMessage(msg); // This will truncate the msg if it's too long.

            // TODO: add obscenity filtering
            // Keep in mind using LINQ would incur allocations which need to be garbage collected.
            // Maybe consider pooling of player collections?

            //LinkedList<Player> filteredSet = null;
            //if (_obscene != null)
            //    filteredSet = ObsceneFilter(set);


            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref to, 1)).Slice(0, length);
            _network?.SendToSet(set, bytes, flags);

            //if(_chatNet != null)

            //if (filteredSet != null &&
            //    _obscene != null)//&&
            //                     //!_obscene.Filter(msg) ||
            //{
            //    if (_net != null)
            //        _net.SendToSet(filteredSet, bytes, flags);

            //    //if(_chatNet != null)
            //}
        }

        //private LinkedList<Player> ObsceneFilter(LinkedList<Player> set)
        //{
        //    LinkedList<Player> filteredSet = null;
        //    LinkedListNode<Player> node;
        //    LinkedListNode<Player> nextNode = set.First;

        //    while ((node = nextNode) != null)
        //    {
        //        nextNode = node.Next;

        //        if(node.Value.Flags.ObscenityFilter)
        //        {
        //            set.Remove(node);

        //            if (filteredSet == null)
        //                filteredSet = new LinkedList<Player>();

        //            filteredSet.AddLast(node);
        //        }
        //    }

        //    return filteredSet;
        //}

        private static string GetChatType(ChatMessageType type) => type switch
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

        private bool Ok(Player p, ChatMessageType messageType)
        {
            if (p == null)
                return false;

            if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                return false;

            ArenaData ad = null;
            p.Arena?.TryGetExtraData(_adKey, out ad);
            
            ChatMask mask;

            lock (pd.Lock)
            {
                ExpireMask(p);

                mask = pd.Mask;

                if (ad != null)
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

        private void ExpireMask(Player p)
        {
            if (p == null)
                return;

            if (!p.TryGetExtraData(_pdKey, out PlayerData pd))
                return;

            DateTime now = DateTime.UtcNow;

            lock (pd.Lock)
            {
                // handle expiring masks
                if (pd.Expires != null
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
    }
}

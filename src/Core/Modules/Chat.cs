using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class Chat : IModule, IChat
    {
        private const char CmdChar1 = '?';
        private const char CmdChar2 = '*';
        private const char MultiChar = '|';
        private const char ModChatChar = '\\';

        private ComponentBroker _broker;
        private IPlayerData _playerData;
        private INetwork _net;
        private IChatNet _chatNet;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IArenaManager _arenaManager;
        private ICommandManager _commandManager;
        private ICapabilityManager _capabilityManager;
        private IPersist _persist;
        private IObscene _obscene;
        private InterfaceRegistrationToken _iChatToken;

        private struct Config
        {
            /// <summary>
            /// Whether to send chat messages reliably.
            /// </summary>
            [ConfigHelp("Chat", "MessageReliable", ConfigScope.Global, typeof(bool), DefaultValue = "1", Description = "Whether to send chat messages reliably.")]
            public bool MessageReliable;

            /// <summary>
            /// How many messages needed to be sent in a short period of time (about a second) to qualify for chat flooding.
            /// </summary>
            [ConfigHelp("Chat", "FloodLimit", ConfigScope.Global, typeof(int), DefaultValue = "10", Description = "How many messages needed to be sent in a short period of time (about a second) to qualify for chat flooding.")]
            public int FloodLimit;

            /// <summary>
            /// How many seconds to disable chat for a player that is flooding chat messages.
            /// </summary>
            [ConfigHelp("Chat", "FloodShutup", ConfigScope.Global, typeof(int), DefaultValue = "60", Description = "How many seconds to disable chat for a player that is flooding chat messages.")]
            public int FloodShutup;

            /// <summary>
            /// How many commands are allowed on a single line.
            /// </summary>
            [ConfigHelp("Chat", "CommandLimit", ConfigScope.Global, typeof(int), DefaultValue = "5", Description = "How many commands are allowed on a single line.")]
            public int CommandLimit;
        }

        private Config _cfg;

        private int _cmkey;
        private int _pmkey;

        private readonly object _playerMaskLock = new object();

        private class ArenaChatMask
        {
            public ChatMask mask;
        }

        private class PlayerChatMask
        {
            public ChatMask mask;

            /// <summary>
            /// null for a session long mask
            /// </summary>
            public DateTime? expires;

            /// <summary>
            /// a count of messages. this decays exponentially 50% per second
            /// </summary>
            public int msgs;

            public DateTime lastCheck;
        }

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IPlayerData playerData,
            INetwork net,
            IConfigManager configManager,
            ILogManager logManager,
            IArenaManager arenaManager,
            ICommandManager commandManager,
            ICapabilityManager capabilityManager)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _net = net ?? throw new ArgumentNullException(nameof(net));
            //_chatNet = chatNet ?? throw new ArgumentNullException(nameof(chatNet));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            //_persist = persist ?? throw new ArgumentNullException(nameof(persist));
            //_obscene = obscene ?? throw new ArgumentNullException(nameof(obscene));

            _cmkey = _arenaManager.AllocateArenaData<ArenaChatMask>();
            _pmkey = _playerData.AllocatePlayerData<PlayerChatMask>();

            //if(_persist != null)
                //_persist.

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            PlayerActionCallback.Register(_broker, Callback_PlayerAction);

            _cfg.MessageReliable = _configManager.GetInt(_configManager.Global, "Chat", "MessageReliable", 1) != 0;
            _cfg.FloodLimit = _configManager.GetInt(_configManager.Global, "Chat", "FloodLimit", 10);
            _cfg.FloodShutup = _configManager.GetInt(_configManager.Global, "Chat", "FloodShutup", 60);
            _cfg.CommandLimit = _configManager.GetInt(_configManager.Global, "Chat", "CommandLimit", 5);

            _net.AddPacket(C2SPacketType.Chat, Packet_Chat);

            //if(_chatNet != null)
            //_chatNet.

            _iChatToken = _broker.RegisterInterface<IChat>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<IChat>(ref _iChatToken) != 0)
                return false;

            _net.RemovePacket(C2SPacketType.Chat, Packet_Chat);

            //if(_chatNet != null)
                //_chatNet.

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
            PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);

            //if(_persist != null)
                //_persist.

            _arenaManager.FreeArenaData(_cmkey);
            _playerData.FreePlayerData(_pmkey);

            return true;
        }

        #endregion

        #region IChat Members

        void IChat.SendMessage(Player p, string format, params object[] args)
        {
            SendMessage(p, format, args);
        }

        void IChat.SendCmdMessage(Player p, string format, params object[] args)
        {
            SendMessage(p, format, args);
        }

        void IChat.SendSetMessage(IEnumerable<Player> set, string format, params object[] args)
        {
            SendMessage(set, ChatMessageType.Arena, ChatSound.None, null, format, args);
        }

        void IChat.SendSoundMessage(Player p, ChatSound sound, string format, params object[] args)
        {
            Player[] set = { p };
            SendMessage(set, ChatMessageType.Arena, sound, null, format, args);
        }

        void IChat.SendSetSoundMessage(IEnumerable<Player> set, ChatSound sound, string format, params object[] args)
        {
            SendMessage(set, ChatMessageType.Arena, sound, null, format, args);
        }

        void IChat.SendAnyMessage(IEnumerable<Player> set, ChatMessageType type, ChatSound sound, Player from, string format, params object[] args)
        {
            SendMessage(set, type, sound, from, format, args);
        }

        void IChat.SendArenaMessage(Arena arena, string format, params object[] args)
        {
            IEnumerable<Player> set = GetArenaSet(arena, null);
            if (set != null)
                SendMessage(set, ChatMessageType.Arena, ChatSound.None, null, format, args);
        }

        private LinkedList<Player> GetArenaSet(Arena arena, Player except)
        {
            LinkedList<Player> set = new LinkedList<Player>();
            _playerData.Lock();
            try
            {
                foreach (Player p in _playerData.PlayerList)
                {
                    if (p.Status == PlayerState.Playing &&
                        (p.Arena == arena || arena == null) && 
                        p != except)
                    {
                        set.AddLast(p);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            return set;
        }

        private LinkedList<Player> GetCapabilitySet(string capability, Player except)
        {
            LinkedList<Player> set = new LinkedList<Player>();
            _playerData.Lock();
            try
            {
                foreach (Player p in _playerData.PlayerList)
                {
                    if (p.Status == PlayerState.Playing &&
                        _capabilityManager.HasCapability(p, capability) &&
                        p != except)
                    {
                        set.AddLast(p);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            return set;
        }

        void IChat.SendArenaSoundMessage(Arena arena, ChatSound sound, string format, params object[] args)
        {
            IEnumerable<Player> set = GetArenaSet(arena, null);
            if (set != null)
                SendMessage(set, ChatMessageType.Arena, sound, null, format, args);
        }

        void IChat.SendModMessage(string format, params object[] args)
        {
            IEnumerable<Player> set = GetCapabilitySet(Constants.Capabilities.ModChat, null);
            if (set != null)
                SendMessage(set, ChatMessageType.SysopWarning, ChatSound.None, null, format, args);
        }

        void IChat.SendRemotePrivMessage(IEnumerable<Player> set, ChatSound sound, string squad, string sender, string message)
        {
            string text = !string.IsNullOrWhiteSpace(squad)
                ? $"({squad})({sender})>{message}"
                : $"({sender})>{message}";

            if (text.Length > ChatPacket.MaxMessageLength)
                text = text.Substring(0, ChatPacket.MaxMessageLength);

            ChatPacket cp = new ChatPacket();
            cp.Type = (byte)C2SPacketType.Chat;
            cp.ChatType = (byte)ChatMessageType.RemotePrivate;
            cp.Sound = (byte)sound;
            cp.PlayerId = -1;
            int length = ChatPacket.HeaderLength + cp.SetMessage(text);

            _net.SendToSet(
                set,
                MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref cp, 1)).Slice(0, length),
                NetSendFlags.Reliable);

            //if(_chatNet != null && 
        }

        ChatMask IChat.GetArenaChatMask(Arena arena)
        {
            if(arena == null)
                return new ChatMask();

            if (!(arena[_cmkey] is ArenaChatMask am))
                return new ChatMask();

            lock (_playerMaskLock)
            {
                return am.mask;
            }
        }

        void IChat.SetArenaChatMask(Arena arena, ChatMask mask)
        {
            if (arena == null)
                return;

            if (!(arena[_cmkey] is ArenaChatMask am))
                return;

            lock (_playerMaskLock)
            {
                am.mask = mask;
            }
        }

        ChatMask IChat.GetPlayerChatMask(Player p)
        {
            if (p == null)
                return new ChatMask();

            if (!(p[_pmkey] is PlayerChatMask pm))
                return new ChatMask();

            lock (_playerMaskLock)
            {
                return pm.mask;
            }
        }

        void IChat.SetPlayerChatMask(Player p, ChatMask mask, int timeout)
        {
            if (p == null)
                return;

            if (!(p[_pmkey] is PlayerChatMask pm))
                return;

            lock (_playerMaskLock)
            {
                pm.mask = mask;

                if (timeout == 0)
                    pm.expires = null;
                else
                    pm.expires = DateTime.UtcNow.AddSeconds(timeout);
            }
        }

        void IChat.SendWrappedText(Player p, string text)
        {
            if (p == null)
                return;

            if (string.IsNullOrWhiteSpace(text))
                return;

            foreach (ReadOnlySpan<char> line in text.GetWrappedText(78, ' '))
            {
                SendMessage(p, string.Concat("  ", line));
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

            if (!(p[_pmkey] is PlayerChatMask pm))
                return;

            if (action == PlayerAction.PreEnterArena)
            {
                lock (_playerMaskLock)
                {
                    pm.mask.Clear();
                    pm.expires = null;
                    pm.msgs = 0;
                    pm.lastCheck = DateTime.UtcNow;
                }
            }
        }

        private void Packet_Chat(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len < 6) // Note: ASSS also checks if > 500
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "bad chat packet len={0}", len);
                return;
            }

            Arena arena = p.Arena;
            if (arena == null || p.Status != PlayerState.Playing)
                return;

            ref ChatPacket from = ref MemoryMarshal.AsRef<ChatPacket>(data);

            // Null terminate if it isn't already.
            // Also, truncate the message if the length is over the limit that we will allow.
            from.MessageBytes[Math.Min(len - ChatPacket.HeaderLength, from.MessageBytes.Length) - 1] = 0;

            // remove control characters from the chat message
            RemoveControlCharacters(from.MessageBytes);

            ChatSound sound = ChatSound.None;
            if(_capabilityManager.HasCapability(p, Constants.Capabilities.SoundMessages))
                sound = (ChatSound)from.Sound;

            // TODO: Don't convert to string, instead pass the Span<byte> around or maybe decode to a stackallocated Span<char>?
            string text = from.MessageBytes.ReadNullTerminatedString();

            Player target;
            switch ((ChatMessageType)from.ChatType)
            {
                case ChatMessageType.Arena:
                    _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "received arena message");
                    break;

                case ChatMessageType.PubMacro:
                case ChatMessageType.Pub:
                    if (from.MessageBytes[0] == ModChatChar)
                        HandleModChat(p, text.Substring(1), sound);
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
                        _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "cross-arena nmefreq chat message");
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
                        _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "cross-arena private chat message");
                    break;

                case ChatMessageType.RemotePrivate:
                    HandleRemotePrivate(p, text, false, sound);
                    break;

                case ChatMessageType.SysopWarning:
                    _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "received sysop message");
                    break;

                case ChatMessageType.Chat:
                    HandleChat(p, text, sound);
                    break;

                default:
                    _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "received undefined type {0} chat message", from.ChatType);
                    break;
            }

            CheckFlood(p);
        }

        private static void RemoveControlCharacters(Span<byte> characters)
        {
            for (int i = 0; i < characters.Length && characters[i] != 0; i++)
            {
                // Note: For some reason ASSS also removes 255 (ÿ). Not doing the same here.
                if (characters[i] < 32 // C0 control characters
                    || characters[i] == 127) // DEL
                {
                    characters[i] = (byte)'_';
                }
            }
        }

        private void CheckFlood(Player p)
        {
            if (!(p[_pmkey] is PlayerChatMask pm))
                return;

            lock (_playerMaskLock)
            {
                pm.msgs++;

                // TODO: add capability to spam (for bots)
                if (pm.msgs >= _cfg.FloodLimit && 
                    _cfg.FloodLimit > 0 &&
                    !_capabilityManager.HasCapability(p, Constants.Capabilities.CanSpam))
                {
                    pm.msgs >>= 1;

                    if (pm.expires != null)
                    {
                        // already has a mask, add time
                        pm.expires.Value.AddSeconds(_cfg.FloodShutup);
                    }
                    else if (pm.mask.IsClear)
                    {
                        // only set expiry time if this is a new shutup
                        pm.expires = DateTime.UtcNow.AddSeconds(_cfg.FloodShutup);
                    }

                    pm.mask.SetRestricted(ChatMessageType.PubMacro);
                    pm.mask.SetRestricted(ChatMessageType.Pub);
                    pm.mask.SetRestricted(ChatMessageType.Freq);
                    pm.mask.SetRestricted(ChatMessageType.EnemyFreq);
                    pm.mask.SetRestricted(ChatMessageType.Private);
                    pm.mask.SetRestricted(ChatMessageType.RemotePrivate);
                    pm.mask.SetRestricted(ChatMessageType.Chat);
                    pm.mask.SetRestricted(ChatMessageType.ModChat);
                    pm.mask.SetRestricted(ChatMessageType.BillerCommand);

                    SendMessage(p, "You have been shut up for {0} seconds for flooding.", _cfg.FloodShutup);
                    _logManager.LogP(LogLevel.Info, nameof(Chat), p, "flooded chat, shut up for {0} seconds", _cfg.FloodShutup);
                }
            }
        }

        private void SendMessage(Player p, string format, params object[] args)
        {
            Player[] set = { p };
            SendMessage(set, ChatMessageType.Arena, ChatSound.None, null, format, args);
        }

        private void SendMessage(IEnumerable<Player> set, ChatMessageType type, ChatSound sound, Player from, string format, params object[] args)
        {
            string text = (args != null && args.Length > 0)
                ? string.Format(format, args)
                : format;

            if (text.Length > ChatPacket.MaxMessageLength)
                text = text.Substring(0, ChatPacket.MaxMessageLength);

            if (type == ChatMessageType.ModChat)
                type = ChatMessageType.SysopWarning;

            ChatPacket cp = new ChatPacket();
            cp.Type = (byte)S2CPacketType.Chat;
            cp.ChatType = (byte)type;
            cp.Sound = (byte)sound;
            cp.PlayerId = from != null ? (short)from.Id : (short)-1;
            int length = ChatPacket.HeaderLength + cp.SetMessage(text);

            _net.SendToSet(
                set,
                MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref cp, 1)).Slice(0, length),
                NetSendFlags.Reliable);

            //if(_chatNet != null && 
        }

        private void HandleChat(Player p, string text, ChatSound sound)
        {
            if (p == null)
                return;

            if (string.IsNullOrEmpty(text))
                return;

            if (Ok(p, ChatMessageType.Chat))
            {
                // msg should look like "text" or "#;text"
                FireChatMessageCallback(null, p, ChatMessageType.Chat, sound, null, -1, text);
#if CFG_LOG_PRIVATE
                _logManager.LogP(LogLevel.Drivel, "Chat", p, "chat msg: {0}", text);
#endif
            }
        }

        private void HandleRemotePrivate(Player p, string text, bool isAllCmd, ChatSound sound)
        {
            if (p == null)
                return;

            if (string.IsNullOrEmpty(text))
                return;

            string[] tokens = text.Split(':', 3, StringSplitOptions.None);
            if (text[0] != ':' || tokens.Length != 3 || tokens[0] != string.Empty || tokens[1] == string.Empty || tokens[2] == string.Empty)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "malformed remote private message");
                return;
            }

            string dest = tokens[1];
            string message = tokens[2];

            if ((IsCommandChar(message[0]) && message.Length > 1) || isAllCmd)
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

                    LinkedList<Player> set = new LinkedList<Player>();
                    set.AddLast(d);

                    SendReply(set, ChatMessageType.RemotePrivate, sound, p, -1, $"({p.Name})>{message}", p.Name.Length + 3);
                }

                FireChatMessageCallback(null, p, ChatMessageType.RemotePrivate, sound, d, -1, d != null ? message : text);

#if CFG_LOG_PRIVATE
                _logManager.LogP(LogLevel.Drivel, "Chat", p, "to [{0}] remote priv: {1}", dest, message);
#endif
            }
        }

        private void HandlePrivate(Player p, Player dst, string text, bool isAllCmd, ChatSound sound)
        {
            if (p == null)
                return;

            if (string.IsNullOrEmpty(text))
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
                LinkedList<Player> set = new LinkedList<Player>();
                set.AddLast(dst);
                SendReply(set, ChatMessageType.Private, sound, p, p.Id, text, 0);

                FireChatMessageCallback(arena, p, ChatMessageType.Private, sound, null, -1, text);
#if CFG_LOG_PRIVATE
                _logManager.LogP(LogLevel.Drivel, "Chat", p, "to [{0}] priv msg: {1}", dst.Name, text);
#endif
            }
        }

        private void FireChatMessageCallback(Arena arena, Player playerFrom, ChatMessageType type, ChatSound sound, Player playerTo, short freq, string message)
        {
            // if we have an arena, then call the arena's callbacks, otherwise do the global ones
            if (arena != null)
                ChatMessageCallback.Fire(arena, playerFrom, type, sound, playerTo, freq, message);
            else
                ChatMessageCallback.Fire(_broker, playerFrom, type, sound, playerTo, freq, message);
        }

        private void HandleFreq(Player p, short freq, string text, ChatSound sound)
        {
            if (p == null)
                return;

            if (string.IsNullOrEmpty(text))
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
                    LinkedList<Player> set = null;
                    foreach (Player i in _playerData.PlayerList)
                    {
                        if (i.Freq == freq &&
                            i.Arena == arena &&
                            i != p)
                        {
                            if (set == null)
                                set = new LinkedList<Player>();

                            set.AddLast(i);
                        }
                    }

                    if (set == null)
                        return;

                    SendReply(set, type, sound, p, p.Id, text, 0);

                    FireChatMessageCallback(arena, p, type, sound, null, freq, text);
                    _logManager.LogP(LogLevel.Drivel, nameof(Chat), p, "freq msg ({0}): {1}", freq, text);
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

        private void HandleModChat(Player p, string message, ChatSound sound)
        {
            if (_capabilityManager == null)
            {
                SendMessage(p, "Staff chat is currently disabled");
                return;
            }

            if(_capabilityManager.HasCapability(p, Constants.Capabilities.SendModChat) && Ok(p, ChatMessageType.ModChat))
            {
                LinkedList<Player> set = GetCapabilitySet(Constants.Capabilities.ModChat, p);
                if (set != null)
                {
                    message = p.Name + "> " + message;
                    SendReply(set, ChatMessageType.ModChat, sound, p, p.Id, message, p.Name.Length + 2);
                    FireChatMessageCallback(null, p, ChatMessageType.ModChat, sound, null, -1, message);
                    _logManager.LogP(LogLevel.Drivel, nameof(Chat), p, "mod chat: {0}", message);
                }
            }
            else
            {
                SendMessage(p, "You aren't allowed to use the staff chat. If you need to send a message to the zone staff, use ?cheater.");
                _logManager.LogP(LogLevel.Drivel, nameof(Chat), p, "attempted mod chat (missing cap or shutup): {0}", message);
            }
        }

        private void HandlePub(Player p, string msg, bool isMacro, bool isAllCmd, ChatSound sound)
        {
            if (p == null)
                return;

            if (string.IsNullOrEmpty(msg))
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
                    LinkedList<Player> set = GetArenaSet(arena, p);
                    if(set != null)
                        SendReply(set, type, sound, p, p.Id, msg, 0);

                    FireChatMessageCallback(arena, p, type, sound, null, -1, msg);
                    _logManager.LogP(LogLevel.Drivel, nameof(Chat), p, "pub msg: {0}", msg);
                }
            }

        }

        private void RunCommands(string msg, Player p, ITarget target, ChatSound sound)
        {
            if (msg == null)
                throw new ArgumentNullException("msg");
            
            if (msg == string.Empty)
                return;

            if (target == null)
                throw new ArgumentNullException("target");

            // skip initial ? or *
            if (IsCommandChar(msg[0]))
            {
                msg = msg.Remove(0, 1);

                if(msg == string.Empty)
                    return;
            }

            bool multi = msg[0] == MultiChar;

            if (multi)
            {
                string[] tokens = msg.Split(new char[] { MultiChar }, StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < tokens.Length && i < _cfg.CommandLimit; i++)
                {
                    // give modules a chance to rewrite the command
                    // TODO:

                    // run the command
                    _commandManager.Command(tokens[i], p, target, sound);
                }
            }
            else
            {
                // give modules a chance to rewrite the command
                // TODO:

                // run the command
                _commandManager.Command(msg, p, target, sound);
            }
        }

        private void SendReply(LinkedList<Player> set, ChatMessageType type, ChatSound sound, Player p, int fromPid, string msg, int chatNetOffset)
        {
            NetSendFlags flags = NetSendFlags.None;
            if (type == ChatMessageType.PubMacro)
                flags |= NetSendFlags.PriorityN1;

            if (_cfg.MessageReliable)
                flags |= NetSendFlags.Reliable;

            if (msg.Length > ChatPacket.MaxMessageLength)
                msg = msg.Substring(0, ChatPacket.MaxMessageLength);

            if (type == ChatMessageType.ModChat)
                type = ChatMessageType.SysopWarning;

            ChatPacket to = new ChatPacket();
            to.Type = (byte)S2CPacketType.Chat;
            to.ChatType = (byte)type;
            to.Sound = (byte)sound;
            to.PlayerId = (short)fromPid;
            int length = ChatPacket.HeaderLength + to.SetMessage(msg);

            LinkedList<Player> filteredSet = null;
            if (_obscene != null)
                filteredSet = ObsceneFilter(set);

            Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref to, 1)).Slice(0, length);

            _net.SendToSet(set, bytes, flags);

            //if(_chatNet != null)

            if (filteredSet != null &&
                _obscene != null)//&&
                                 //!_obscene.Filter(msg) ||
            {
                if (_net != null)
                    _net.SendToSet(filteredSet, bytes, flags);

                //if(_chatNet != null)
            }
        }

        private LinkedList<Player> ObsceneFilter(LinkedList<Player> set)
        {
            LinkedList<Player> filteredSet = null;
            LinkedListNode<Player> node;
            LinkedListNode<Player> nextNode = set.First;

            while ((node = nextNode) != null)
            {
                nextNode = node.Next;

                if(node.Value.Flags.ObscenityFilter)
                {
                    set.Remove(node);
                    
                    if (filteredSet == null)
                        filteredSet = new LinkedList<Player>();

                    filteredSet.AddLast(node);
                }
            }

            return filteredSet;
        }

        private string GetChatType(ChatMessageType type)
        {
            return type switch
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
        }

        private bool Ok(Player p, ChatMessageType messageType)
        {
            if (p == null)
                return false;

            if (!(p[_pmkey] is PlayerChatMask pm))
                return false;

            ArenaChatMask am = (p.Arena != null) ? p.Arena[_cmkey] as ArenaChatMask : null;
            ChatMask mask;

            lock (_playerMaskLock)
            {
                ExpireMask(p);

                if (am != null)
                    pm.mask.Combine(am.mask);

                mask = pm.mask;
            }

            return mask.IsAllowed(messageType);
        }

        private void ExpireMask(Player p)
        {
            if (p == null)
                return;

            if (!(p[_pmkey] is PlayerChatMask pm))
                return;

            DateTime now = DateTime.UtcNow;

            // handle expiring masks
            if(pm.expires != null)
                if (now > pm.expires)
                {
                    pm.mask.Clear();
                    pm.expires = null;
                }

            // handle exponential decay of msg count
            int d = (int)((now - pm.lastCheck).TotalMilliseconds / 1000);
            if (d > 31)
                d = 31;

            if (d < 0)
                d = 0; // really shouldn't happen but just in case...

            pm.msgs >>= d;
            pm.lastCheck = now;
        }
    }
}

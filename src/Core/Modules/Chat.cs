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
            public bool msgrel;
            public int floodlimit;
            public int floodshutup;
            public int cmdlimit;
        }

        private Config _cfg;

        private int _cmkey;
        private int _pmkey;

        private object _playerMaskLock = new object();

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

            ArenaActionCallback.Register(_broker, arenaAction);
            PlayerActionCallback.Register(_broker, playerAction);

            _cfg.msgrel = _configManager.GetInt(_configManager.Global, "Chat", "MessageReliable", 1) != 0;
            _cfg.floodlimit = _configManager.GetInt(_configManager.Global, "Chat", "FloodLimit", 10);
            _cfg.floodshutup = _configManager.GetInt(_configManager.Global, "Chat", "FloodShutup", 60);
            _cfg.cmdlimit = _configManager.GetInt(_configManager.Global, "Chat", "CommandLimit", 5);

            if (_net != null)
                _net.AddPacket((int)C2SPacketType.Chat, onRecievePlayerChatPacket);

            //if(_chatNet != null)
            //_chatNet.

            _iChatToken = _broker.RegisterInterface<IChat>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<IChat>(ref _iChatToken) != 0)
                return false;

            if (_net != null)
                _net.RemovePacket((int)C2SPacketType.Chat, onRecievePlayerChatPacket);

            //if(_chatNet != null)
                //_chatNet.

            ArenaActionCallback.Unregister(_broker, arenaAction);
            PlayerActionCallback.Unregister(_broker, playerAction);

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
            sendMessage(p, format, args);
        }

        void IChat.SendCmdMessage(Player p, string format, params object[] args)
        {
            sendMessage(p, format, args);
        }

        void IChat.SendSetMessage(IEnumerable<Player> set, string format, params object[] args)
        {
            sendMessage(set, ChatMessageType.Arena, ChatSound.None, null, format, args);
        }

        void IChat.SendSoundMessage(Player p, ChatSound sound, string format, params object[] args)
        {
            Player[] set = { p };
            sendMessage(set, ChatMessageType.Arena, sound, null, format, args);
        }

        void IChat.SendSetSoundMessage(IEnumerable<Player> set, ChatSound sound, string format, params object[] args)
        {
            sendMessage(set, ChatMessageType.Arena, sound, null, format, args);
        }

        void IChat.SendAnyMessage(IEnumerable<Player> set, ChatMessageType type, ChatSound sound, Player from, string format, params object[] args)
        {
            
        }

        void IChat.SendArenaMessage(Arena arena, string format, params object[] args)
        {
            IEnumerable<Player> set = getArenaSet(arena, null);
            if (set != null)
                sendMessage(set, ChatMessageType.Arena, ChatSound.None, null, format, args);
        }

        private LinkedList<Player> getArenaSet(Arena arena, Player except)
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

        private LinkedList<Player> getCapabilitySet(string capability, Player except)
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
            IEnumerable<Player> set = getArenaSet(arena, null);
            if (set != null)
                sendMessage(set, ChatMessageType.Arena, sound, null, format, args);
        }

        void IChat.SendModMessage(string format, params object[] args)
        {
            IEnumerable<Player> set = getCapabilitySet(Constants.Capabilities.ModChat, null);
            if (set != null)
                sendMessage(set, ChatMessageType.SysopWarning, ChatSound.None, null, format, args);
        }

        void IChat.SendRemotePrivMessage(IEnumerable<Player> set, ChatSound sound, string squad, string sender, string message)
        {
            using (DataBuffer buf = Pool<DataBuffer>.Default.Get())
            {
                string text;
                int size;

                if (squad != null)
                {
                    text = string.Format("({0})({1})>{2}", squad, sender, message);
                    if (text.Length > 250)
                        text = text.Substring(0, 250);
                }
                else
                {
                    text = string.Format("({0})>{1}", sender, message);
                    if (text.Length > 250)
                        text = text.Substring(0, 250);
                }

                ChatPacket cp = new ChatPacket(buf.Bytes);
                cp.PkType = (byte)C2SPacketType.Chat;
                cp.Type = (byte)ChatMessageType.RemotePrivate;
                cp.Sound = (byte)sound;
                cp.Pid = -1;
                size = ChatPacket.HeaderLength + cp.SetText(text);

                if (_net != null)
                    _net.SendToSet(set, buf.Bytes, size, NetSendFlags.Reliable);

                //if(_chatNet != null && 
            }
        }

        ChatMask IChat.GetArenaChatMask(Arena arena)
        {
            if(arena == null)
                return new ChatMask();

            ArenaChatMask am = arena[_cmkey] as ArenaChatMask;
            if (am == null)
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

            ArenaChatMask am = arena[_cmkey] as ArenaChatMask;
            if (am == null)
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

            PlayerChatMask pm = p[_pmkey] as PlayerChatMask;
            if(pm == null)
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

            PlayerChatMask pm = p[_pmkey] as PlayerChatMask;
            if (pm == null)
                return;

            lock (_playerMaskLock)
            {
                pm.mask = mask;

                if (timeout == 0)
                    pm.expires = null;
                else
                    pm.expires = DateTime.Now.AddSeconds(timeout);
            }
        }

        void IChat.SendWrappedText(Player p, string text)
        {
            if (p == null)
                return;

            if (string.IsNullOrWhiteSpace(text))
                return;

            foreach (string str in text.Trim().WrapText(78))
            {
                sendMessage(p, "  {0}", str);
            }
        }

        #endregion

        private void arenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null)
                return;

            if (action == ArenaAction.Create || action == ArenaAction.ConfChanged)
            {
                // TODO: settings for mask
            }
        }

        private void playerAction(Player p, PlayerAction action, Arena arena)
        {
            if (p == null)
                return;

            PlayerChatMask pm = p[_pmkey] as PlayerChatMask;
            if (pm == null)
                return;

            if (action == PlayerAction.PreEnterArena)
            {
                lock (_playerMaskLock)
                {
                    pm.mask.Clear();
                    pm.expires = null;
                    pm.msgs = 0;
                    pm.lastCheck = DateTime.Now;
                }
            }
        }

        private void onRecievePlayerChatPacket(Player p, byte[] data, int len)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len < 6 || len > 500)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "bad chat packet len={0}", len);
                return;
            }

            Arena arena = p.Arena;
            if (arena == null || p.Status != PlayerState.Playing)
                return;

            ChatPacket from = new ChatPacket(data);
            from.RemoveControlCharactersFromText(len);

            ChatSound sound = ChatSound.None;
            if(_capabilityManager.HasCapability(p, Constants.Capabilities.SoundMessages))
                sound = (ChatSound)from.Sound;

            string text = from.GetText(len);

            Player target;
            switch ((ChatMessageType)from.Type)
            {
                case ChatMessageType.Arena:
                    _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "recieved arena message");
                    break;

                case ChatMessageType.PubMacro:
                case ChatMessageType.Pub:
                    if (text[0] == ModChatChar)
                        handleModChat(p, text.Substring(1), sound);
                    else
                        handlePub(p, text, from.Type == (byte)ChatMessageType.PubMacro, false, sound);
                    break;

                case ChatMessageType.EnemyFreq:
                    target = _playerData.PidToPlayer(from.Pid);
                    if (target == null)
                        break;

                    if (target.Arena == arena)
                        handleFreq(p, target.Freq, text, sound);
                    else
                        _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "cross-arena nmefreq chat message");
                    break;

                case ChatMessageType.Freq:
                    handleFreq(p, p.Freq, text, sound);
                    break;

                case ChatMessageType.Private:
                    target = _playerData.PidToPlayer(from.Pid);
                    if (target == null)
                        break;

                    if (target.Arena == arena)
                        handlePrivate(p, target, text, false, sound);
                    else
                        _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "cross-arena private chat message");
                    break;

                case ChatMessageType.RemotePrivate:
                    handleRemotePrivate(p, text, false, sound);
                    break;

                case ChatMessageType.SysopWarning:
                    _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "recieved sysop message");
                    break;

                case ChatMessageType.Chat:
                    handleChat(p, text, sound);
                    break;

                default:
                    _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "recieved undefined type {0} chat message", from.Type);
                    break;
            }

            checkFlood(p);
        }

        private void checkFlood(Player p)
        {
            PlayerChatMask pm = p[_pmkey] as PlayerChatMask;
            if (pm == null)
                return;

            lock (_playerMaskLock)
            {
                pm.msgs++;

                // TODO: add capability to spam (for bots)
                if (pm.msgs >= _cfg.floodlimit && 
                    _cfg.floodlimit > 0 &&
                    !_capabilityManager.HasCapability(p, Constants.Capabilities.CanSpam))
                {
                    pm.msgs >>= 1;

                    if (pm.expires != null)
                    {
                        // already has a mask, add time
                        pm.expires.Value.AddSeconds(_cfg.floodshutup);
                    }
                    else if (pm.mask.IsClear)
                    {
                        // only set expiry time if this is a new shutup
                        pm.expires = DateTime.Now.AddSeconds(_cfg.floodshutup);
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

                    sendMessage(p, "You have been shut up for {0} seconds for flooding.", _cfg.floodshutup);
                    _logManager.LogP(LogLevel.Info, nameof(Chat), p, "flooded chat, shut up for {0} seconds", _cfg.floodshutup);
                }
            }
        }

        private void sendMessage(Player p, string format, params object[] args)
        {
            Player[] set = { p };
            sendMessage(set, ChatMessageType.Arena, ChatSound.None, null, format, args);
        }

        private void sendMessage(IEnumerable<Player> set, ChatMessageType type, ChatSound sound, Player from, string format, params object[] args)
        {
            using (DataBuffer buf = Pool<DataBuffer>.Default.Get())
            {
                string text;
                if (args != null && args.Length > 0)
                    text = string.Format(format, args);
                else
                    text = format;

                if (text.Length > 250)
                    text = text.Substring(0, 250);

                if (type == ChatMessageType.ModChat)
                    type = ChatMessageType.SysopWarning;

                ChatPacket cp = new ChatPacket(buf.Bytes);
                cp.PkType = (byte)S2CPacketType.Chat;
                cp.Type = (byte)type;
                cp.Sound = (byte)sound;
                cp.Pid = from != null ? (short)from.Id : (short)-1;
                int size = ChatPacket.HeaderLength + cp.SetText(text);

                if (_net != null)
                    _net.SendToSet(set, buf.Bytes, size, NetSendFlags.Reliable);

                //if(_chatNet != null && 
            }
        }

        private void handleChat(Player p, string text, ChatSound sound)
        {
            if (p == null)
                return;

            if (string.IsNullOrEmpty(text))
                return;

            if (ok(p, ChatMessageType.Chat))
            {
                // msg should look like "text" or "#;text"
                fireChatMessageEvent(null, p, ChatMessageType.Chat, sound, null, -1, text);
#if CFG_LOG_PRIVATE
                _logManager.LogP(LogLevel.Drivel, "Chat", p, "chat msg: {0}", text);
#endif
            }
        }

        private void handleRemotePrivate(Player p, string text, bool isAllCmd, ChatSound sound)
        {
            if (p == null)
                return;

            if (string.IsNullOrEmpty(text))
                return;

            string[] tokens = text.Split(new char[] {':'}, StringSplitOptions.None);
            if (text[0] != ':' || tokens.Length != 3 || tokens[0] != string.Empty || tokens[1] == string.Empty || tokens[2] == string.Empty)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Chat), p, "malformed remote private message");
                return;
            }

            string dest = tokens[1];
            string message = tokens[2];

            if ((isCommandChar(message[0]) && message.Length > 1) || isAllCmd)
            {
                if (ok(p, ChatMessageType.Command))
                {
                    Player d = _playerData.FindPlayer(dest);
                    if (d != null && d.Status == PlayerState.Playing)
                    {
                        ITarget target = d;
                        runCommands(message, p, target, sound);
                    }
                }
            }
            else if (ok(p, ChatMessageType.RemotePrivate))
            {
                Player d = _playerData.FindPlayer(dest);
                if (d != null)
                {
                    if (d.Status != PlayerState.Playing)
                        return;

                    LinkedList<Player> set = new LinkedList<Player>();
                    set.AddLast(d);

                    sendReply(set, ChatMessageType.RemotePrivate, sound, p, -1, string.Format("({0})>{1}", p.Name, message), p.Name.Length + 3);
                }

                fireChatMessageEvent(null, p, ChatMessageType.RemotePrivate, sound, d, -1, d != null ? message : text);

#if CFG_LOG_PRIVATE
                _logManager.LogP(LogLevel.Drivel, "Chat", p, "to [{0}] remote priv: {1}", dest, message);
#endif
            }
        }

        private void handlePrivate(Player p, Player dst, string text, bool isAllCmd, ChatSound sound)
        {
            if (p == null)
                return;

            if (string.IsNullOrEmpty(text))
                return;

            Arena arena = p.Arena; // this can be null

            if ((isCommandChar(text[0]) && text.Length > 1) || isAllCmd)
            {
                if (ok(p, ChatMessageType.Command))
                {
                    ITarget target = dst;
                    runCommands(text, p, target, sound);
                }
            }
            else if (ok(p, ChatMessageType.Private))
            {
                LinkedList<Player> set = new LinkedList<Player>();
                set.AddLast(dst);
                sendReply(set, ChatMessageType.Private, sound, p, p.Id, text, 0);

                fireChatMessageEvent(arena, p, ChatMessageType.Private, sound, null, -1, text);
#if CFG_LOG_PRIVATE
                _logManager.LogP(LogLevel.Drivel, "Chat", p, "to [{0}] priv msg: {1}", dst.Name, text);
#endif
            }
        }

        private void fireChatMessageEvent(Arena arena, Player playerFrom, ChatMessageType type, ChatSound sound, Player playerTo, short freq, string message)
        {
            // if we have an arena, then call the arena's callbacks, otherwise do the global ones
            if (arena != null)
                ChatMessageCallback.Fire(arena, playerFrom, type, sound, playerTo, freq, message);
            else
                ChatMessageCallback.Fire(_broker, playerFrom, type, sound, playerTo, freq, message);
        }

        private void handleFreq(Player p, short freq, string text, ChatSound sound)
        {
            if (p == null)
                return;

            if (string.IsNullOrEmpty(text))
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            ChatMessageType type = p.Freq == freq ? ChatMessageType.Freq : ChatMessageType.EnemyFreq;

            if (isCommandChar(text[0]) && text.Length > 1)
            {
                if (ok(p, ChatMessageType.Command))
                {
                    ITarget target = Target.TeamTarget(p.Arena, p.Freq);
                    runCommands(text, p, target, sound);
                }
            }
            else if(ok(p, type))
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

                    sendReply(set, type, sound, p, p.Id, text, 0);

                    fireChatMessageEvent(arena, p, type, sound, null, freq, text);
                    _logManager.LogP(LogLevel.Drivel, nameof(Chat), p, "freq msg ({0}): {1}", freq, text);
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
        }

        private static bool isCommandChar(char c)
        {
            return c == CmdChar1 || c == CmdChar2;
        }

        private void handleModChat(Player p, string message, ChatSound sound)
        {
            if (_capabilityManager == null)
            {
                sendMessage(p, "Staff chat is currently disabled");
                return;
            }

            if(_capabilityManager.HasCapability(p, Constants.Capabilities.SendModChat) && ok(p, ChatMessageType.ModChat))
            {
                LinkedList<Player> set = getCapabilitySet(Constants.Capabilities.ModChat, p);
                if (set != null)
                {
                    message = p.Name + "> " + message;
                    sendReply(set, ChatMessageType.ModChat, sound, p, p.Id, message, p.Name.Length + 2);
                    fireChatMessageEvent(null, p, ChatMessageType.ModChat, sound, null, -1, message);
                    _logManager.LogP(LogLevel.Drivel, nameof(Chat), p, "mod chat: {0}", message);
                }
            }
            else
            {
                sendMessage(p, "You aren't allowed to use the staff chat. If you need to send a message to the zone staff, use ?cheater.");
                _logManager.LogP(LogLevel.Drivel, nameof(Chat), p, "attempted mod chat (missing cap or shutup): {0}", message);
            }
        }

        private void handlePub(Player p, string msg, bool isMacro, bool isAllCmd, ChatSound sound)
        {
            if (p == null)
                return;

            if (string.IsNullOrEmpty(msg))
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            if ((isCommandChar(msg[0]) && (msg.Length > 1)) || isAllCmd)
            {
                if (ok(p, ChatMessageType.Command))
                {
                    ITarget target = arena;
                    runCommands(msg, p, target, sound);
                }
            }
            else
            {
                ChatMessageType type = isMacro ? ChatMessageType.PubMacro : ChatMessageType.Pub;
                if (ok(p, type))
                {
                    LinkedList<Player> set = getArenaSet(arena, p);
                    if(set != null)
                        sendReply(set, type, sound, p, p.Id, msg, 0);

                    fireChatMessageEvent(arena, p, type, sound, null, -1, msg);
                    _logManager.LogP(LogLevel.Drivel, nameof(Chat), p, "pub msg: {0}", msg);
                }
            }

        }

        private void runCommands(string msg, Player p, ITarget target, ChatSound sound)
        {
            if (msg == null)
                throw new ArgumentNullException("msg");
            
            if (msg == string.Empty)
                return;

            if (target == null)
                throw new ArgumentNullException("target");

            // skip initial ? or *
            char initial = '\0';
            if (isCommandChar(msg[0]))
            {
                initial = msg[0];
                msg = msg.Remove(0, 1);

                if(msg == string.Empty)
                    return;
            }

            bool multi = msg[0] == MultiChar;

            if (multi)
            {
                string[] tokens = msg.Split(new char[] { MultiChar }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string token in tokens)
                {
                    // give modules a chance to rewrite the command
                    // TODO:

                    // run the command
                    _commandManager.Command(token, p, target, sound);
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

        private void sendReply(LinkedList<Player> set, ChatMessageType type, ChatSound sound, Player p, int fromPid, string msg, int chatNetOffset)
        {
            using (DataBuffer buf = Pool<DataBuffer>.Default.Get())
            {
                ChatPacket to = new ChatPacket(buf.Bytes);

                NetSendFlags flags = NetSendFlags.None;
                if (type == ChatMessageType.PubMacro)
                    flags |= NetSendFlags.PriorityN1;

                if (_cfg.msgrel)
                    flags |= NetSendFlags.Reliable;

                int maxTextLength = buf.Bytes.Length - ChatPacket.HeaderLength;
                if (msg.Length > maxTextLength)
                    return;

                if (type == ChatMessageType.ModChat)
                    type = ChatMessageType.SysopWarning;

                to.PkType = (byte)S2CPacketType.Chat;
                to.Type = (byte)type;
                to.Sound = (byte)sound;
                to.Pid = (short)fromPid;
                int size = to.SetText(msg) + ChatPacket.HeaderLength;

                LinkedList<Player> filteredSet = null;
                if (_obscene != null)
                    filteredSet = obsceneFilter(set);

                if (_net != null)
                    _net.SendToSet(set, buf.Bytes, size, flags);

                //if(_chatNet != null)

                if (filteredSet != null &&
                    _obscene != null )//&&
                    //!_obscene.Filter(msg) ||
                {
                    if (_net != null)
                        _net.SendToSet(filteredSet, buf.Bytes, size, flags);

                    //if(_chatNet != null)
                }
            }
        }

        private LinkedList<Player> obsceneFilter(LinkedList<Player> set)
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

        private string getChatType(ChatMessageType type)
        {
            switch (type)
            {
                case ChatMessageType.Arena: return "ARENA";
                case ChatMessageType.PubMacro: return "PUBM";
                case ChatMessageType.Pub: return "PUB";
                case ChatMessageType.Freq: return "FREQ";
                case ChatMessageType.EnemyFreq: return "FREQ";
                case ChatMessageType.Private: return "PRIV";
                case ChatMessageType.RemotePrivate: return "PRIV";
                case ChatMessageType.SysopWarning: return "SYSOP";
                case ChatMessageType.Chat: return "CHAT";
                case ChatMessageType.ModChat: return "MOD";
                default: return null;
            }
        }

        private bool ok(Player p, ChatMessageType messageType)
        {
            if (p == null)
                return false;

            PlayerChatMask pm = p[_pmkey] as PlayerChatMask;
            if (pm == null)
                return false;

            ArenaChatMask am = (p.Arena != null) ? p.Arena[_cmkey] as ArenaChatMask : null;
            ChatMask mask;

            lock (_playerMaskLock)
            {
                expireMask(p);

                if (am != null)
                    pm.mask.Combine(am.mask);

                mask = pm.mask;
            }

            return mask.IsAllowed(messageType);
        }

        private void expireMask(Player p)
        {
            if (p == null)
                return;

            PlayerChatMask pm = p[_pmkey] as PlayerChatMask;
            if (pm == null)
                return;

            DateTime now = DateTime.Now;

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

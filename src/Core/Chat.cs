using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using SS.Utilities;

namespace SS.Core
{
    public class Chat : IModule, IChat
    {
        private const char CmdChar1 = '?';
        private const char CmdChar2 = '*';
        private const char MultiChar = '|';
        private const char ModChatChar = '\\';

        private ModuleManager _mm;
        private IPlayerData _playerData;
        private INetwork _net;
        private IChatNet _chatNet;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IArenaManagerCore _arenaManager;
        private ICommandManager _commandManager;
        private ICapabilityManager _capabilityManager;
        private IPersist _persist;
        private IObscene _obscene;

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

        Type[] IModule.InterfaceDependencies
        {
            get
            {
                return new Type[] {
                    typeof(IPlayerData), 
                    typeof(INetwork), 
                    //typeof(IChatNet), 
                    typeof(IConfigManager), 
                    typeof(ILogManager), 
                    typeof(IArenaManagerCore), 
                    //typeof(ICommandManager), 
                    //typeof(ICapabilityManager), 
                    //typeof(IPersist), 
                    //typeof(IObscene), 
                };
            }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;
            _playerData = interfaceDependencies[typeof(IPlayerData)] as IPlayerData;
            _net = interfaceDependencies[typeof(INetwork)] as INetwork;
            //_chatNet = interfaceDependencies[typeof(IChatNet)] as IChatNet;
            _configManager = interfaceDependencies[typeof(IConfigManager)] as IConfigManager;
            _logManager = interfaceDependencies[typeof(ILogManager)] as ILogManager;
            _arenaManager = interfaceDependencies[typeof(IArenaManagerCore)] as IArenaManagerCore;
            //_commandManager = interfaceDependencies[typeof(ICommandManager)] as ICommandManager;
            //_capabilityManager = interfaceDependencies[typeof(ICapabilityManager)] as ICapabilityManager;
            //_persist = interfaceDependencies[typeof(IPersist)] as IPersist;
            //_obscene = interfaceDependencies[typeof(IObscene)] as IObscene;

            _cmkey = _arenaManager.AllocateArenaData<ArenaChatMask>();
            _pmkey = _playerData.AllocatePlayerData<PlayerChatMask>();

            //if(_persist != null)
                //_persist.

            _mm.RegisterCallback<ArenaActionEventHandler>(Constants.Events.ArenaAction, new ArenaActionEventHandler(arenaAction));
            _mm.RegisterCallback<PlayerActionDelegate>(Constants.Events.PlayerAction, new PlayerActionDelegate(playerAction));

            _cfg.msgrel = _configManager.GetInt(_configManager.Global, "Chat", "MessageReliable", 1) != 0;
            _cfg.floodlimit = _configManager.GetInt(_configManager.Global, "Chat", "FloodLimit", 10);
            _cfg.floodshutup = _configManager.GetInt(_configManager.Global, "Chat", "FloodShutup", 60);
            _cfg.cmdlimit = _configManager.GetInt(_configManager.Global, "Chat", "CommandLimit", 5);

            if (_net != null)
                _net.AddPacket((int)C2SPacketType.Chat, onRecievePlayerChatPacket);
            
            //if(_chatNet != null)
                //_chatNet.

            _mm.RegisterInterface<IChat>(this);

            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            _mm.UnregisterInterface<IChat>();

            if (_net != null)
                _net.RemovePacket((int)C2SPacketType.Chat, onRecievePlayerChatPacket);

            //if(_chatNet != null)
                //_chatNet.

            _mm.UnregisterCallback(Constants.Events.ArenaAction, new ArenaActionEventHandler(arenaAction));
            _mm.UnregisterCallback(Constants.Events.PlayerAction, new PlayerActionDelegate(playerAction));

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

        private LinkedList<Player> getCapSet(string cap, Player except)
        {
            // TODO
            return null;
        }

        void IChat.SendArenaSoundMessage(Arena arena, ChatSound sound, string format, params object[] args)
        {
            IEnumerable<Player> set = getArenaSet(arena, null);
            if (set != null)
                sendMessage(set, ChatMessageType.Arena, sound, null, format, args);
        }

        void IChat.SendModMessage(string format, params object[] args)
        {
            IEnumerable<Player> set = getCapSet("seemodchat", null);
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
                    size = text.Length + 6;

                }
                else
                {
                    text = string.Format("({0})>{1}", sender, message);
                    if (text.Length > 250)
                        text = text.Substring(0, 250);
                    size = text.Length + 6;
                }

                ChatPacket cp = new ChatPacket(buf.Bytes, size);
                cp.PkType = (byte)C2SPacketType.Chat;
                cp.Type = (byte)ChatMessageType.RemotePrivate;
                cp.Sound = (byte)sound;
                cp.Pid = -1;
                cp.Text = text;

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
            
        }

        #endregion

        private void arenaAction(Arena arena, ArenaAction action)
        {

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
                _logManager.LogP(LogLevel.Malicious, "Chat", p, "bad chat packet len={0}", len);
                return;
            }

            Arena arena = p.Arena;
            if (arena == null || p.Status != PlayerState.Playing)
                return;

            ChatPacket from = new ChatPacket(data, len);
            from.RemoveControlCharactersFromText();

            ChatSound sound = ChatSound.None;
            // TODO: check capability to send sound messages
            sound = (ChatSound)from.Sound;

            string text = from.Text;

            Player target;
            switch ((ChatMessageType)from.Type)
            {
                case ChatMessageType.Arena:
                    _logManager.LogP(LogLevel.Malicious, "Chat", p, "recieved arena message");
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
                        _logManager.LogP(LogLevel.Malicious, "Chat", p, "cross-arena nmefreq chat message");
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
                        _logManager.LogP(LogLevel.Malicious, "Chat", p, "cross-arena private chat message");
                    break;

                case ChatMessageType.RemotePrivate:
                    handleRemotePrivate(p, text, false, sound);
                    break;

                case ChatMessageType.SysopWarning:
                    _logManager.LogP(LogLevel.Malicious, "Chat", p, "recieved sysop message");
                    break;

                case ChatMessageType.Chat:
                    handleChat(p, text, sound);
                    break;

                default:
                    _logManager.LogP(LogLevel.Malicious, "Chat", p, "recieved undefined type {0} chat message", from.Type);
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
                if (pm.msgs >= _cfg.floodlimit && _cfg.floodlimit > 0)
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
                    _logManager.LogP(LogLevel.Info, "Chat", p, "flooded chat, shut up for {0} seconds", _cfg.floodshutup);
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

                int size = text.Length + ChatPacket.HeaderLength + 1; // +1 to include a null char at the end

                if (type == ChatMessageType.ModChat)
                    type = ChatMessageType.SysopWarning;

                ChatPacket cp = new ChatPacket(buf.Bytes, size);
                cp.PkType = (byte)C2SPacketType.Chat;
                cp.Type = (byte)type;
                cp.Sound = (byte)sound;
                cp.Pid = from != null ? (short)from.Id : (short)-1;

                if (_net != null)
                    _net.SendToSet(set, buf.Bytes, size, NetSendFlags.Reliable);

                //if(_chatNet != null && 
            }
        }

        private void handleChat(Player p, string text, ChatSound sound)
        {
            
        }

        private void handleRemotePrivate(Player p, string text, bool isAllCmd, ChatSound sound)
        {
            
        }

        private void handlePrivate(Player p, Player dst, string text, bool isAllCmd, ChatSound sound)
        {
            
        }

        private void handleFreq(Player p, short freq, string text, ChatSound sound)
        {
            
        }

        private void handleModChat(Player p, string message, ChatSound sound)
        {
            
        }

        private void handlePub(Player p, string msg, bool isMacro, bool isAllCmd, ChatSound sound)
        {
            if (p == null)
                return;

            if (msg == null)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            if ((msg[0] == CmdChar1 || msg[0] == CmdChar2) && msg[1] != '\0' || isAllCmd)
            {
                if (ok(p, ChatMessageType.Command))
                {
                    Target target;
                    // TODO
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

                    arena.DoCallbacks(Constants.Events.ChatMessage, p, type, sound, null, -1, msg);

                    _logManager.LogP(LogLevel.Drivel, "Chat", p, "pub msg: {0}", msg);
                }
            }

        }

        private void sendReply(LinkedList<Player> set, ChatMessageType type, ChatSound sound, Player p, int fromPid, string msg, int chatNetOffset)
        {
            using (DataBuffer buf = Pool<DataBuffer>.Default.Get())
            {
                ChatPacket to = new ChatPacket(buf.Bytes, buf.Bytes.Length);

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
                to.Text = msg;

                LinkedList<Player> filteredSet = null;
                if (_obscene != null)
                    filteredSet = obsceneFilter(set);

                if (_net != null)
                    _net.SendToSet(set, buf.Bytes, ChatPacket.HeaderLength + msg.Length, flags);

                //if(_chatNet != null)

                if (filteredSet != null &&
                    _obscene != null )//&&
                    //!_obscene.Filter(msg) ||
                {
                    if (_net != null)
                        _net.SendToSet(filteredSet, buf.Bytes, ChatPacket.HeaderLength + msg.Length, flags);

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

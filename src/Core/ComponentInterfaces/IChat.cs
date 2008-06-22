using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections.Specialized;
using SS.Utilities;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// types of chat messages
    /// </summary>
    public enum ChatMessageType
    {
        /// <summary>
        /// arena messages (in green)
        /// </summary>
        Arena = 0, 

        /// <summary>
        /// macros as public arena chat
        /// </summary>
        PubMacro = 1, 

        /// <summary>
        /// public arena chat
        /// </summary>
        Pub = 2, 

        /// <summary>
        /// team message
        /// </summary>
        Freq = 3, 

        /// <summary>
        /// team message (alias of Freq)
        /// </summary>
        Team = 3, 

        /// <summary>
        /// enemy team messages
        /// </summary>
        EnemyFreq = 4, 

        /// <summary>
        /// within-arena private messages
        /// </summary>
        Private = 5, 

        /// <summary>
        /// cross-arena or cross-zone private messages
        /// </summary>
        RemotePrivate = 7, 

        /// <summary>
        /// red sysop warning text
        /// </summary>
        SysopWarning = 8, 

        /// <summary>
        /// chat channel messages
        /// </summary>
        Chat = 9, 

        /// <summary>
        /// moderator chat messages (internal only)
        /// </summary>
        ModChat = 10, 

        /// <summary>
        /// msgs that function as commands (internal only)
        /// </summary>
        Command = 11, 

        /// <summary>
        /// commands that go to the biller (internal only)
        /// </summary>
        BillerCommand = 12, 
    }

    /// <summary>
    /// Mask that tells what chat message types are allowed/disallowed.
    /// </summary>
    public struct ChatMask
    {
        private BitVector32 _maskVector;

        public bool IsClear
        {
            get { return _maskVector.Data == 0; }
        }

        public bool IsRestricted(ChatMessageType messageType)
        {
            return _maskVector[BitVector32Masks.GetMask((int)messageType)];
        }

        public bool IsAllowed(ChatMessageType messageType)
        {
            return !IsRestricted(messageType);
        }

        public void SetRestricted(ChatMessageType messageType)
        {
            _maskVector[BitVector32Masks.GetMask((int)messageType)] = true;
        }

        public void SetAllowed(ChatMessageType messageType)
        {
            _maskVector[BitVector32Masks.GetMask((int)messageType)] = false;
        }

        public void Combine(ChatMask chatMask)
        {
            for (int x = 0; x < 32; x++)
            {
                int mask = BitVector32Masks.GetMask(x);
                _maskVector[mask] = _maskVector[mask] || chatMask._maskVector[mask];
            }
        }

        public void Clear()
        {
            _maskVector = new BitVector32(0);
        }
    }

    public interface IChat : IComponentInterface
    {
        /// <summary>
        /// Send a green message to a player
        /// </summary>
        /// <param name="p"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        void SendMessage(Player p, string format, params object[] args);

        /// <summary>
        /// Sends a command response to a player.
        /// For Continuum clients, this is the same as an arena message, but
        /// other clients might interpret them differently.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        void SendCmdMessage(Player p, string format, params object[] args);

        /// <summary>
        /// Sends a green arena message to a set of players.
        /// </summary>
        /// <param name="set"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        void SendSetMessage(IEnumerable<Player> set, string format, params object[] args);

        /// <summary>
        /// Sends a green arena message plus sound code to a player.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="sound"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        void SendSoundMessage(Player p, ChatSound sound, string format, params object[] args);

        /// <summary>
        /// Sends a green arena message plus sound code to a set of players.
        /// </summary>
        /// <param name="set"></param>
        /// <param name="sound"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        void SendSetSoundMessage(IEnumerable<Player> set, ChatSound sound, string format, params object[] args);

        /// <summary>
        /// Sends an arbitrary chat message to a set of players.
        /// </summary>
        /// <param name="set"></param>
        /// <param name="type"></param>
        /// <param name="sound"></param>
        /// <param name="from"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        void SendAnyMessage(IEnumerable<Player> set, ChatMessageType type, ChatSound sound, Player from, string format, params object[] args);

        /// <summary>
        /// Sends a green arena message to all players in an arena.
        /// </summary>
        /// <param name="arena"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        void SendArenaMessage(Arena arena, string format, params object[] args);

        /// <summary>
        /// Sends a green arena message plus sound code to all players in an arena.
        /// </summary>
        /// <param name="arena"></param>
        /// <param name="sound"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        void SendArenaSoundMessage(Arena arena, ChatSound sound, string format, params object[] args);

        /// <summary>
        /// Sends a moderator chat message to all connected staff.
        /// </summary>
        /// <param name="format"></param>
        /// <param name="args"></param>
        void SendModMessage(string format, params object[] args);

        /// <summary>
        /// Sends a remove private message to a set of players.
        /// This should only be used from billing server modules.
        /// </summary>
        /// <param name="set"></param>
        /// <param name="sound"></param>
        /// <param name="squad"></param>
        /// <param name="sender"></param>
        /// <param name="message"></param>
        void SendRemotePrivMessage(IEnumerable<Player> set, ChatSound sound, string squad, string sender, string message);

        /// <summary>
        /// Retrives the chat mask for an arena.
        /// </summary>
        /// <param name="arena"></param>
        /// <returns></returns>
        ChatMask GetArenaChatMask(Arena arena);

        /// <summary>
        /// Sets the chat mask for an arena.
        /// </summary>
        /// <param name="arena"></param>
        /// <param name="mask"></param>
        void SetArenaChatMask(Arena arena, ChatMask mask);

        /// <summary>
        /// Retrives the chat mask for a player.
        /// </summary>
        /// <param name="p"></param>
        ChatMask GetPlayerChatMask(Player p);

        /// <summary>
        /// Sets the chat mask for a player.
        /// </summary>
        /// <param name="p">the player whose mask to modify</param>
        /// <param name="mask">the new chat mask</param>
        /// <param name="timeout">zero to set a session mask (valid until the next arena change), or a number of seconds for the mask to be valid</param>
        void SetPlayerChatMask(Player p, ChatMask mask, int timeout);

        /// <summary>
        /// A utility function for sending lists of items in a chat message.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="text"></param>
        void SendWrappedText(Player p, string text);
    }
}

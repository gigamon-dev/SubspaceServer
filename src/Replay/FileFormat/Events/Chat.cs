using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Chat
    {
        #region Static members

        public static readonly int Length;

        static Chat()
        {
            Length = Marshal.SizeOf(typeof(Chat));
        }

        #endregion

        public EventHeader Header;
        private short playerId;
        private byte type;
        private byte sound;
        private ushort messageLength;
        // The message bytes come next, but are not part of this struct since it varies in length.

        public Chat(ServerTick ticks, short playerId, ChatMessageType type, ChatSound sound, ushort messageLength)
        {
            Header = new(ticks, EventType.Chat);
            this.playerId = LittleEndianConverter.Convert(playerId);
            this.type = (byte)type;
            this.sound = (byte)sound;
            this.messageLength = LittleEndianConverter.Convert(messageLength);
        }

        #region Helper properties

        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        public ChatMessageType Type
        {
            get => (ChatMessageType)LittleEndianConverter.Convert(type);
            set => type = (byte)value;
        }

        public ChatSound Sound
        {
            get => (ChatSound)LittleEndianConverter.Convert(sound);
            set => sound = (byte)value;
        }

        public ushort MessageLength
        {
            get => LittleEndianConverter.Convert(messageLength);
            set => messageLength = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}

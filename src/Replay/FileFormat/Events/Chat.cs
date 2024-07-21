using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Chat(ServerTick ticks, short playerId, ChatMessageType type, ChatSound sound, ushort messageLength)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<Chat>();

        #endregion

        public EventHeader Header = new(ticks, EventType.Chat);
        private short playerId = LittleEndianConverter.Convert(playerId);
        private byte type = (byte)type;
        private byte sound = (byte)sound;
        private ushort messageLength = LittleEndianConverter.Convert(messageLength);
        // The message bytes come next, but are not part of this struct since it varies in length.

        #region Helper properties

        public short PlayerId
        {
            readonly get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        public ChatMessageType Type
        {
            readonly get => (ChatMessageType)LittleEndianConverter.Convert(type);
            set => type = (byte)value;
        }

        public ChatSound Sound
        {
            readonly get => (ChatSound)LittleEndianConverter.Convert(sound);
            set => sound = (byte)value;
        }

        public ushort MessageLength
        {
            readonly get => LittleEndianConverter.Convert(messageLength);
            set => messageLength = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}

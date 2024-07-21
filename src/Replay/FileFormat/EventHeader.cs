using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EventHeader(ServerTick ticks, EventType type)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<EventHeader>();

        #endregion

        private uint ticks = LittleEndianConverter.Convert(ticks);
        private short type = LittleEndianConverter.Convert((short)type);

        #region Helper properties

        public ServerTick Ticks
        {
            readonly get => LittleEndianConverter.Convert(ticks);
            set => ticks = LittleEndianConverter.Convert(value);
        }

        public EventType Type
        {
            readonly get => (EventType)LittleEndianConverter.Convert(type);
            set => type = LittleEndianConverter.Convert((short)value);
        }

        #endregion
    }
}

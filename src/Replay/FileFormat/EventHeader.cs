using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EventHeader
    {
        #region Static members

        public static readonly int Length;

        static EventHeader()
        {
            Length = Marshal.SizeOf(typeof(EventHeader));
        }

        #endregion

        private uint ticks;
        private short type;

        public EventHeader(ServerTick ticks, EventType type)
        {
            this.ticks = LittleEndianConverter.Convert(ticks);
            this.type = LittleEndianConverter.Convert((short)type);
        }

        #region Helper properties

        public ServerTick Ticks
        {
            get => LittleEndianConverter.Convert(ticks);
            set => ticks = LittleEndianConverter.Convert(value);
        }

        public EventType Type
        {
            get => (EventType)LittleEndianConverter.Convert(type);
            set => type = LittleEndianConverter.Convert((short)value);
        }

        #endregion
    }
}

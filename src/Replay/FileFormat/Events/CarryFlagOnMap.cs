using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CarryFlagOnMap
    {
        #region Static members

        public static readonly int Length;

        static CarryFlagOnMap()
        {
            Length = Marshal.SizeOf(typeof(CarryFlagOnMap));
        }

        #endregion

        public EventHeader Header;
        private short flagId;
        private short x;
        private short y;
        private short freq;

        public CarryFlagOnMap(ServerTick ticks, short flagId, short x, short y, short freq)
        {
            Header = new(ticks, EventType.CarryFlagOnMap);
            this.flagId = LittleEndianConverter.Convert(flagId);
            this.x = LittleEndianConverter.Convert(x);
            this.y = LittleEndianConverter.Convert(y);
            this.freq = LittleEndianConverter.Convert(freq);
        }

        #region Helper properties

        public short FlagId => LittleEndianConverter.Convert(flagId);
        public short X => LittleEndianConverter.Convert(x);
        public short Y => LittleEndianConverter.Convert(y);
        public short Freq => LittleEndianConverter.Convert(freq);

        #endregion
    }
}

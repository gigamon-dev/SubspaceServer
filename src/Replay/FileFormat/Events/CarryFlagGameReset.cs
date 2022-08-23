using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    /// <summary>
    /// Event for when the carry flag game should be reset.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CarryFlagGameReset
    {
        #region Static members

        public static readonly int Length;

        static CarryFlagGameReset()
        {
            Length = Marshal.SizeOf(typeof(CarryFlagGameReset));
        }

        #endregion

        public EventHeader Header;
        private short freq;
        private int points;

        public CarryFlagGameReset(ServerTick ticks, short freq, int points)
        {
            Header = new(ticks, EventType.CarryFlagGameReset);
            this.freq = LittleEndianConverter.Convert(freq);
            this.points = LittleEndianConverter.Convert(points);
        }

        #region Helper properties

        public short Freq => LittleEndianConverter.Convert(freq);
        public int Points => LittleEndianConverter.Convert(points);

        #endregion
    }
}

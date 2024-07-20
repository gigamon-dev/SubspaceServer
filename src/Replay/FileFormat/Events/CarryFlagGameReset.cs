using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    /// <summary>
    /// Event for when the carry flag game should be reset.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CarryFlagGameReset(ServerTick ticks, short freq, int points)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<CarryFlagGameReset>();

        #endregion

        public EventHeader Header = new(ticks, EventType.CarryFlagGameReset);
        private short freq = LittleEndianConverter.Convert(freq);
        private int points = LittleEndianConverter.Convert(points);

		#region Helper properties

		public short Freq
		{
			readonly get => LittleEndianConverter.Convert(freq);
			set => freq = LittleEndianConverter.Convert(value);
		}

		public int Points
		{
			readonly get => LittleEndianConverter.Convert(points);
			set => points = LittleEndianConverter.Convert(value);
		}

		#endregion
	}
}

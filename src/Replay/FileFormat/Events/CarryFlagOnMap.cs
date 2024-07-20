using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CarryFlagOnMap(ServerTick ticks, short flagId, short x, short y, short freq)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<CarryFlagOnMap>();

        #endregion

        public EventHeader Header = new(ticks, EventType.CarryFlagOnMap);
        private short flagId = LittleEndianConverter.Convert(flagId);
        private short x = LittleEndianConverter.Convert(x);
        private short y = LittleEndianConverter.Convert(y);
        private short freq = LittleEndianConverter.Convert(freq);

		#region Helper properties

		public short FlagId
		{
			readonly get => LittleEndianConverter.Convert(flagId);
			set => flagId = LittleEndianConverter.Convert(value);
		}

		public short X
		{
			readonly get => LittleEndianConverter.Convert(x);
			set => x = LittleEndianConverter.Convert(value);
		}

		public short Y
		{
			readonly get => LittleEndianConverter.Convert(y);
			set => y = LittleEndianConverter.Convert(value);
		}

		public short Freq
		{
			readonly get => LittleEndianConverter.Convert(freq);
			set => freq = LittleEndianConverter.Convert(value);
		}

		#endregion
	}
}

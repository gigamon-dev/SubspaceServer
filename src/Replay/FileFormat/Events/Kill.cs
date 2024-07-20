using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Kill(ServerTick ticks, short killer, short killed, short points, short flags)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<Kill>();

        #endregion

        public EventHeader Header = new(ticks, EventType.Kill);
        private short killer = LittleEndianConverter.Convert(killer);
        private short killed = LittleEndianConverter.Convert(killed);
        private short points = LittleEndianConverter.Convert(points);
        private short flags = LittleEndianConverter.Convert(flags);

		#region Helper properties

		public short Killer
		{
			readonly get => LittleEndianConverter.Convert(killer);
			set => killer = LittleEndianConverter.Convert(value);
		}

		public short Killed
		{
			readonly get => LittleEndianConverter.Convert(killed);
			set => killed = LittleEndianConverter.Convert(value);
		}

		public short Points
		{
			readonly get => LittleEndianConverter.Convert(points);
			set => points = LittleEndianConverter.Convert(value);
		}

		public short Flags
		{
			readonly get => LittleEndianConverter.Convert(flags);
			set => flags = LittleEndianConverter.Convert(value);
		}

		#endregion
	}
}

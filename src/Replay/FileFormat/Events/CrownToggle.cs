using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CrownToggle(ServerTick ticks, bool on, short playerId)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<CrownToggle>();

        #endregion

        public EventHeader Header = new(ticks, on ? EventType.CrownToggleOn : EventType.CrownToggleOff);
        private short playerId = LittleEndianConverter.Convert(playerId);

		#region Helper properties

		public short PlayerId
		{
			readonly get => LittleEndianConverter.Convert(playerId);
			set => playerId = LittleEndianConverter.Convert(value);
		}

		#endregion
	}
}

using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FreqChange(ServerTick ticks, short playerId, short newFreq)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<FreqChange>();

        #endregion

        public EventHeader Header = new(ticks, EventType.FreqChange);
        private short playerId = LittleEndianConverter.Convert(playerId);
        private short newFreq = LittleEndianConverter.Convert(newFreq);

		#region Helper properties

		public short PlayerId
		{
			readonly get => LittleEndianConverter.Convert(playerId);
			set => playerId = LittleEndianConverter.Convert(value);
		}

		public short NewFreq
		{
			readonly get => LittleEndianConverter.Convert(newFreq);
			set => newFreq = LittleEndianConverter.Convert(value);
		}

		#endregion
	}
}

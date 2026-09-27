using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2SLatencyEstimateChange(ServerTick ticks, short playerId, uint c2sLatencyEstimate)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<C2SLatencyEstimateChange>();

        #endregion

        public EventHeader Header = new(ticks, EventType.C2SLatencyEstimateChanged);
        private short playerId = LittleEndianConverter.Convert(playerId);
        private uint c2sLatencyEstimate = LittleEndianConverter.Convert(c2sLatencyEstimate);

        #region Helper properties

        public short PlayerId
        {
            readonly get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        public uint C2SLatencyEstimate
        {
            readonly get => LittleEndianConverter.Convert(c2sLatencyEstimate);
            set => c2sLatencyEstimate = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}

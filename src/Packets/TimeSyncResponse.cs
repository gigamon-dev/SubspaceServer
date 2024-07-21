using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct TimeSyncResponse(uint clientTime, uint serverTime)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<TimeSyncResponse>();

        #endregion

        public readonly byte T1 = 0x00;
        public readonly byte T2 = 0x06;
        private readonly uint clientTime = LittleEndianConverter.Convert(clientTime);
        private readonly uint serverTime = LittleEndianConverter.Convert(serverTime);

        #region Helper Properties

        public uint ClientTime => LittleEndianConverter.Convert(clientTime);

        public uint ServerTime => LittleEndianConverter.Convert(serverTime);

        #endregion
    }
}
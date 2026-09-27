using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct TimeSyncResponse(uint requestTime, uint responseTime)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<TimeSyncResponse>();

        #endregion

        public readonly byte T1 = 0x00;
        public readonly byte T2 = 0x06;
        private readonly uint requestTime = LittleEndianConverter.Convert(requestTime);
        private readonly uint responseTime = LittleEndianConverter.Convert(responseTime);

        #region Helper Properties

        /// <summary>
        /// The time from the time sync request, <see cref="TimeSyncRequest.Time"/>.
        /// </summary>
        public uint RequestTime => LittleEndianConverter.Convert(requestTime);

        /// <summary>
        /// The time that this response was sent.
        /// </summary>
        public uint ResponseTime => LittleEndianConverter.Convert(responseTime);

        #endregion
    }
}
using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct TimeSyncRequest(uint time, uint packetsSent, uint packetsReceived)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<TimeSyncRequest>();

        #endregion

        public readonly byte T1 = 0x00;
        public readonly byte T2 = 0x05;
        private readonly uint time = LittleEndianConverter.Convert(time);
        private readonly uint packetsSent = LittleEndianConverter.Convert(packetsSent);
        private readonly uint packetsReceived = LittleEndianConverter.Convert(packetsReceived);

        #region Helper properties

        public uint Time => LittleEndianConverter.Convert(time);

        public uint PacketsSent => LittleEndianConverter.Convert(packetsSent);

        public uint PacketsReceived => LittleEndianConverter.Convert(packetsReceived);

        #endregion
    }
}

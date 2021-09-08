using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TimeSyncC2SPacket
    {
        public static readonly int Length;

        static TimeSyncC2SPacket()
        {
            Length = Marshal.SizeOf<TimeSyncC2SPacket>();
        }

        public byte T1;
        public byte T2;
        private uint time;
        private uint pktsent;
        private uint pktrecvd;

        public uint Time
        {
            get { return LittleEndianConverter.Convert(time); }
            set { time = LittleEndianConverter.Convert(value); }
        }

        public uint PktSent
        {
            get { return LittleEndianConverter.Convert(pktsent); }
            set { pktsent = LittleEndianConverter.Convert(value); }
        }

        public uint PktRecvd
        {
            get { return LittleEndianConverter.Convert(pktrecvd); }
            set { pktrecvd = LittleEndianConverter.Convert(value); }
        }
    }
}

using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TimeSyncRequest
    {
        public static readonly int Length;

        static TimeSyncRequest()
        {
            Length = Marshal.SizeOf<TimeSyncRequest>();
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

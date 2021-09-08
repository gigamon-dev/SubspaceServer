using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct AckPacket
    {
        public static readonly int Length;

        static AckPacket()
        {
            Length = Marshal.SizeOf<AckPacket>();
        }

        public readonly byte T1;
        public readonly byte T2;
        private readonly int seqNum;

        public int SeqNum
        {
            get { return LittleEndianConverter.Convert(seqNum); }
        }

        public AckPacket(int seqNum)
        {
            T1 = 0;
            T2 = 4;
            this.seqNum = LittleEndianConverter.Convert(seqNum);
        }
    }
}

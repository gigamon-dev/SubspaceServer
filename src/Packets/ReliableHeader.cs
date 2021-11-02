using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ReliableHeader
    {
        public static readonly int Length;

        static ReliableHeader()
        {
            Length = Marshal.SizeOf<ReliableHeader>();
        }

        public byte T1;
        public byte T2;
        private int seqNum;

        public int SeqNum
        {
            get => LittleEndianConverter.Convert(seqNum);
            set => seqNum = LittleEndianConverter.Convert(value);
        }

        public void Initialize(int seqNum)
        {
            T1 = 0x00;
            T2 = 0x03;
            this.seqNum = LittleEndianConverter.Convert(seqNum);
        }
    }
}

using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct ReliableHeader(int seqNum)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<ReliableHeader>();

        #endregion

        public readonly byte T1 = 0x00;
        public readonly byte T2 = 0x03;
        private readonly int seqNum = LittleEndianConverter.Convert(seqNum);

        #region Helper Properties

        public int SeqNum => LittleEndianConverter.Convert(seqNum);

        #endregion
    }
}

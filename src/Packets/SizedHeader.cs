using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SizedHeader
    {
        public static readonly int Length;

        static SizedHeader()
        {
            Length = Marshal.SizeOf<SizedHeader>();
        }

        public byte T1;
        public byte T2;
        private int size;

        public int Size
        {
            get => LittleEndianConverter.Convert(size);
            set => size = LittleEndianConverter.Convert(value);
        }
    }
}

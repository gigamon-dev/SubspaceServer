using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PresizedHeader
    {
        public static readonly int Length;

        static PresizedHeader()
        {
            Length = Marshal.SizeOf<PresizedHeader>();
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

using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct SizedHeader(int size)
    {
		#region Static Members

		public static readonly int Length = Marshal.SizeOf<SizedHeader>();

        #endregion

        public readonly byte T1 = 0x00;
        public readonly byte T2 = 0x0A;
        private readonly int size = LittleEndianConverter.Convert(size);

		#region Helper Properties

		public int Size => LittleEndianConverter.Convert(size);

        #endregion
    }
}

using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_WarpTo(short x, short y)
	{
		#region Static Members

		public static readonly int Length = Marshal.SizeOf<S2C_WarpTo>();

		#endregion

		public readonly byte Type = (byte)S2CPacketType.WarpTo;
        private readonly short x = LittleEndianConverter.Convert(x);
        private readonly short y = LittleEndianConverter.Convert(y);

		#region Helper Properties

		public readonly short X => LittleEndianConverter.Convert(x);

		public readonly short Y => LittleEndianConverter.Convert(y);

		#endregion
	}
}

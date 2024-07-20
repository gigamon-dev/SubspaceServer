using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Map
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct RegionAutoWarpChunk(short x, short y)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<RegionAutoWarpChunk>();

		#endregion

		private readonly short x = LittleEndianConverter.Convert(x);
		private readonly short y = LittleEndianConverter.Convert(y);
		// This can be followed by an optional 16 bytes containing the destination arena name.
		// If the warp does not cross arenas it can be excluded.
		// Therefore, it's not included in this struct, but read separately.

		#region Helpers

		public short X => LittleEndianConverter.Convert(x);

		public short Y => LittleEndianConverter.Convert(y);

		#endregion
	}
}

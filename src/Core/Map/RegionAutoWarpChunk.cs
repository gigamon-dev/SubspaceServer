using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Map
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RegionAutoWarpChunk
    {
        #region Static members

        public static readonly int Length;

        static RegionAutoWarpChunk()
        {
            Length = Marshal.SizeOf<RegionAutoWarpChunk>();
        }

        #endregion

        private short x;
        private short y;
        // This can be followed by an optional 16 bytes containing the destination arena name.
        // If the warp does not cross arenas it can be excluded.
        // Therefore, it's not included in this struct, but read separately.

        #region Helpers

        public short X
        {
            get => LittleEndianConverter.Convert(x);
            set => x = LittleEndianConverter.Convert(value);
        }

        public short Y
        {
            get => LittleEndianConverter.Convert(y);
            set => y = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}

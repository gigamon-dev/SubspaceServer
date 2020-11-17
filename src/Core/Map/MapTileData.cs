namespace SS.Core.Map
{
    /// <summary>
    /// Format of tile information in an lvl file.
    /// </summary>
    public struct MapTileData
    {
        private uint bitfield;

        private const uint XMask    = 0b_00000000_00000000_00001111_11111111;
        private const uint YMask    = 0b_00000000_11111111_11110000_00000000;
        private const uint TypeMask = 0b_11111111_00000000_00000000_00000000;

        public short X
        {
            get { return (short)(bitfield & XMask); }
            set { bitfield = (bitfield & ~XMask) | ((uint)value & XMask); }
        }

        public short Y
        {
            get { return (short)((bitfield & YMask) >> 12); }
            set { bitfield = (bitfield & ~YMask) | (((uint)value << 12) & YMask); }
        }

        public byte Type
        {
            get { return (byte)((bitfield & TypeMask) >> 24); }
            set { bitfield = (bitfield & ~TypeMask) | (((uint)value << 24) & TypeMask); }
        }
    }
}

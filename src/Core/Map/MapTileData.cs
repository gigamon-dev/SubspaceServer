using SS.Utilities;

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

        private uint BitField
        {
            get { return LittleEndianConverter.Convert(bitfield); }
            set { bitfield = LittleEndianConverter.Convert(value); }
        }

        public short X
        {
            get { return (short)(BitField & XMask); }
            set { BitField = (BitField & ~XMask) | ((uint)value & XMask); }
        }

        public short Y
        {
            get { return (short)((BitField & YMask) >> 12); }
            set { BitField = (BitField & ~YMask) | (((uint)value << 12) & YMask); }
        }

        public byte Type
        {
            get { return (byte)((BitField & TypeMask) >> 24); }
            set { BitField = (BitField & ~TypeMask) | (((uint)value << 24) & TypeMask); }
        }
    }
}

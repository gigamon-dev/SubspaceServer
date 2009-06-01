using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using SS.Utilities;

namespace SS.Core.Map
{
    /// <summary>
    /// to read tile information from an lvl file
    /// </summary>
    public struct MapTileData
    {
        static MapTileData()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            bitfield = locationBuilder.CreateUInt32DataLocation();
            Length = locationBuilder.NumBytes;

            BitFieldBuilder builder = new BitFieldBuilder(32);
            x = (UInt32BitFieldLocation)builder.CreateBitFieldLocation(12);
            y = (UInt32BitFieldLocation)builder.CreateBitFieldLocation(12);
            type = (UInt32BitFieldLocation)builder.CreateBitFieldLocation(8);
        }

        private static readonly UInt32DataLocation bitfield;
        public static readonly int Length;

        private static readonly UInt32BitFieldLocation x;
        private static readonly UInt32BitFieldLocation y;
        private static readonly UInt32BitFieldLocation type;

        private readonly byte[] data;
        private readonly int offset;

        public MapTileData(byte[] data, int offset)
        {
            this.data = data;
            this.offset = offset;
        }

        private uint BitField
        {
            get { return bitfield.GetValue(data, offset); }
        }

        public uint X
        {
            get { return x.GetValue(BitField); }
        }

        public uint Y
        {
            get { return y.GetValue(BitField); }
        }

        public uint Type
        {
            get { return type.GetValue(BitField); }
        }
    }
}

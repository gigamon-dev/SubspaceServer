using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using SS.Utilities;

namespace SS.Core.Map
{
    /// <summary>
    /// header to a chunk of metadata in an extended lvl file
    /// </summary>
    public struct ChunkHeader
    {
        static ChunkHeader()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateUInt32DataLocation();
            size = locationBuilder.CreateUInt32DataLocation();
            Length = locationBuilder.NumBytes;
        }

        private static readonly UInt32DataLocation type;
        private static readonly UInt32DataLocation size;
        public static readonly int Length;

        private readonly byte[] data;
        private readonly int offset;

        public ChunkHeader(byte[] data)
            : this(data, 0)
        {
        }

        public ChunkHeader(byte[] data, int offset)
        {
            this.data = data;
            this.offset = offset;
        }

        public uint Type
        {
            get { return type.GetValue(data, offset); }
        }

        public uint Size
        {
            get { return size.GetValue(data, offset); }
        }

        public ArraySegment<byte> Data
        {
            get { return new ArraySegment<byte>(data, offset + Length, (int)Size); }
        }

        public ArraySegment<byte> DataWithHeader
        {
            get { return new ArraySegment<byte>(data, offset, Length + (int)Size); }
        }
    }
}

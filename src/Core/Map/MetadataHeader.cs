using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using SS.Utilities;

namespace SS.Core.Map
{
    /// <summary>
    /// extended lvl format metadata header (bitmap header's Reserved bytes point us to this header)
    /// </summary>
    public struct MetadataHeader
    {
        static MetadataHeader()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            magic = locationBuilder.CreateDataLocation(4);
            totalsize = locationBuilder.CreateDataLocation(4);
            res1 = locationBuilder.CreateDataLocation(4);
            Length = locationBuilder.NumBytes;
        }

        private static readonly UInt32DataLocation magic;
        private static readonly UInt32DataLocation totalsize;
        private static readonly UInt32DataLocation res1;
        public static readonly int Length;

        /// <summary>
        /// the magic # that should be in the metadata, this is how we validate that the data being read is the header
        /// <remarks>this magic value is an illegal value for tile data.  e.g. an lvl file that doesn't contain a tileset (bmp image)</remarks>
        /// </summary>
        public const uint MetadataMagic = 0x6c766c65;

        private readonly byte[] data;
        private readonly int offset;

        public MetadataHeader(byte[] data, int offset)
        {
            this.data = data;
            this.offset = offset;
        }

        /// <summary>
        /// way to verify that the data being read is in fact a metadata header
        /// </summary>
        public uint Magic
        {
            get { return magic.GetValue(data, offset); }
        }

        /// <summary>
        /// total size of the metadata, includes the metadata header itself
        /// </summary>
        public uint TotalSize
        {
            get { return totalsize.GetValue(data, offset); }
        }

        public uint Reserved1
        {
            get { return res1.GetValue(data, offset); }
        }
    }
}

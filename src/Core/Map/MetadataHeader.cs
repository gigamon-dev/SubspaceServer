using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Map
{
    /// <summary>
    /// extended lvl format metadata header
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MetadataHeader
    {
        private uint magic;
        private uint totalSize;
        private uint reserved;

        /// <summary>
        /// Should always be <see cref="MetadataMagic"/>.
        /// </summary>
        public uint Magic
        {
            get { return LittleEndianConverter.Convert(magic); }
            set { magic = LittleEndianConverter.Convert(value); }
        }

        /// <summary>
        /// the number of bytes in the whole metadata section,
        /// including the header.note that the tile data will start at an offset of
        /// totalsize bytes from the start of the metadata_header
        /// </summary>
        public uint TotalSize
        {
            get { return LittleEndianConverter.Convert(totalSize); }
            set { totalSize = LittleEndianConverter.Convert(value); }
        }

        public uint Reserved
        {
            get { return LittleEndianConverter.Convert(reserved); }
            set { reserved = LittleEndianConverter.Convert(value); }
        }

        /// <summary>
        /// The magic # that identifies a metadata header.
        /// </summary>
        /// <remarks>
        /// "elvl" in ASCII.
        /// </remarks>
        public const uint MetadataMagic = 0x6c766c65;
    }
}

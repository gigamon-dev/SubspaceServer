using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Map
{
    /// <summary>
    /// header to a chunk of metadata in an extended lvl file
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChunkHeader
    {
        private uint type;
        private uint size;

        /// <summary>
        /// describes what this chunk represents
        /// </summary>
        public uint Type
        {
            get { return LittleEndianConverter.Convert(type); }
            set { type = LittleEndianConverter.Convert(value); }
        }

        /// <summary>
        /// the number of bytes in the data portion of this chunk, _not_ including the header.
        /// </summary>
        public uint Size
        {
            get { return LittleEndianConverter.Convert(size); }
            set { size = LittleEndianConverter.Convert(value); }
        }
    }
}

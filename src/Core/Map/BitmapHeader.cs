using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Map
{
    /// <summary>
    /// to read the header of a bitmap file
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BitmapHeader
    {
        private ushort bm;
        private uint fileSize;
        private uint reserved;
        private uint offset;

        public ushort BM
        {
            get { return LittleEndianConverter.Convert(bm); }
            set { bm = LittleEndianConverter.Convert(value); }
        }

        public uint FileSize
        {
            get { return LittleEndianConverter.Convert(fileSize); }
            set { fileSize = LittleEndianConverter.Convert(value); }
        }

        public uint Reserved
        {
            get { return LittleEndianConverter.Convert(reserved); }
            set { reserved = LittleEndianConverter.Convert(value); }
        }

        public uint Offset
        {
            get { return LittleEndianConverter.Convert(offset); }
            set { offset = LittleEndianConverter.Convert(value); }
        }
    }
}

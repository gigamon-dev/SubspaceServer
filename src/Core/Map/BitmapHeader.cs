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
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<BitmapHeader>();

        #endregion

        private ushort bm;
        private uint fileSize;
        private uint reserved;
        private uint offset;

        #region Helper Properties

        public ushort BM
        {
            readonly get => LittleEndianConverter.Convert(bm);
            set => bm = LittleEndianConverter.Convert(value);
        }

        public uint FileSize
        {
            readonly get => LittleEndianConverter.Convert(fileSize);
            set => fileSize = LittleEndianConverter.Convert(value);
        }

        public uint Reserved
        {
            readonly get => LittleEndianConverter.Convert(reserved);
            set => reserved = LittleEndianConverter.Convert(value);
        }

        public uint Offset
        {
            readonly get => LittleEndianConverter.Convert(offset);
            set => offset = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}

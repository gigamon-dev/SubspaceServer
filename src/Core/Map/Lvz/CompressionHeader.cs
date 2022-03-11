using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Map.Lvz
{
    /// <summary>
    /// The compression header within a LVZ file.
    /// </summary>
    /// <remarks>
    /// This struct does not include the filename field even though it it part of the header.
    /// This is because the filename is a not a fixed width. It is null-terminated.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CompressionHeader
    {
        public const uint LvzMagic = 0x544e4f43; // CONT

        public static int Length;

        static CompressionHeader()
        {
            Length = Marshal.SizeOf<CompressionHeader>();
        }

        private uint magic;
        public uint Magic => LittleEndianConverter.Convert(magic);

        private uint decompressSize;
        public uint DecompressSize => LittleEndianConverter.Convert(decompressSize);

        private uint fileTime;
        public uint FileTime => LittleEndianConverter.Convert(fileTime);

        private uint compressedSize;
        public uint CompressedSize => LittleEndianConverter.Convert(compressedSize);

        // null-terminated file name
    }
}

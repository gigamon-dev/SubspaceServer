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
    public readonly struct CompressionHeader
    {
        #region Static Members

        public const uint LvzMagic = 0x544e4f43; // CONT

        public static readonly int Length = Marshal.SizeOf<CompressionHeader>();

        #endregion

        private readonly uint magic;
        private readonly uint decompressSize;
        private readonly uint fileTime;
        private readonly uint compressedSize;
        // Followed by a null-terminated file name of variable length

        #region Helper Properties

        public uint Magic => LittleEndianConverter.Convert(magic);

        public uint DecompressSize => LittleEndianConverter.Convert(decompressSize);

        public uint FileTime => LittleEndianConverter.Convert(fileTime);

        public uint CompressedSize => LittleEndianConverter.Convert(compressedSize);

        #endregion
    }
}

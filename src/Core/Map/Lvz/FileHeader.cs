using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Map.Lvz
{
    /// <summary>
    /// The header to a LVZ file. That is, the first 4 bytes of every LVZ file.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FileHeader
    {
        public const uint LvzMagic = 0x544e4f43; // CONT

        public static int Length;

        static FileHeader()
        {
            Length = Marshal.SizeOf<FileHeader>();
        }

        private uint magic;
        public uint Magic => LittleEndianConverter.Convert(magic);

        private int compressedSectionCount;
        public int CompressedSectionCount => LittleEndianConverter.Convert(compressedSectionCount);
    }
}

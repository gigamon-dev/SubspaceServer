using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Map.Lvz
{
    /// <summary>
    /// The header to a LVZ file. That is, the first 4 bytes of every LVZ file.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct FileHeader
    {
		#region Static Members

		public const uint LvzMagic = 0x544e4f43; // CONT

        public static readonly int Length = Marshal.SizeOf<FileHeader>();

        #endregion

        private readonly uint magic;
		private readonly int compressedSectionCount;

		#region Helper Properties

		public uint Magic => LittleEndianConverter.Convert(magic);

        public int CompressedSectionCount => LittleEndianConverter.Convert(compressedSectionCount);

        #endregion
    }
}

using SS.Utilities;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct MapFilenamePacket
    {
        public const int MaxLvzFiles = 16;
        private const int MaxFiles = MaxLvzFiles + 1; // +1 for map file

        // Type
        public byte Type;

        // Files
        private fixed byte filesBytes[File.Length * MaxFiles];
        private Span<File> Files => new Span<File>(Unsafe.AsPointer(ref filesBytes[0]), MaxFiles);

        public void Initialize()
        {
            Type = (byte)S2CPacketType.MapFilename;
        }

        /// <summary>
        /// Sets file info for the original, non-continuum version of the packet, 
        /// which doesn't include <see cref="File.Size"/> and can only contain 1 entry, the .lvl file.
        /// This is for VIE clients (non-bot) that don't support the continuum version of the packet (.lvl file only).
        /// </summary>
        /// <param name="fileName">The .lvl file name.</param>
        /// <param name="checksum">The checksum of the file.</param>
        /// <returns>Number of bytes for the packet.</returns>
        public int SetFileInfo(string fileName, uint checksum)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(fileName));

            ref File file = ref Files[0];
            file.FileName.WriteNullPaddedASCII(fileName);
            file.Checksum = checksum;

            return 1 + File.LengthWithoutSize;
        }

        /// <summary>
        /// Sets file info for a continuum version of the packet which includes <see cref="File.Size"/> and contain multiple entries 
        /// which can contain one .lvl file and and <see cref="MaxLvzFiles"/> .lvz files.
        /// </summary>
        /// <param name="fileIndex">Index of the file to set.</param>
        /// <param name="fileName">The file name (lvl or lvz).</param>
        /// <param name="checksum">The checksum of the file.</param>
        /// <param name="size">The size of the file.</param>
        /// <returns>Number of bytes for the packet to include the file info.</returns>
        public int SetFileInfo(int fileIndex, string fileName, uint checksum, uint size)
        {
            if (fileIndex >= MaxFiles)
                throw new ArgumentOutOfRangeException(nameof(fileIndex), ">= " + MaxFiles);

            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(fileName));

            ref File file = ref Files[fileIndex];
            file.FileName.WriteNullPaddedASCII(fileName);
            file.Checksum = checksum;
            file.Size = size;

            return 1 + ((fileIndex + 1) * File.Length);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public unsafe struct File
        {
            // File Name
            private const int NumFileName = 16;
            private fixed byte fileName[NumFileName];
            public Span<byte> FileName => new Span<byte>(Unsafe.AsPointer(ref fileName[0]), NumFileName);

            // Checksum
            public uint Checksum;

            // Size (continuum only)
            public uint Size;

            public const int Length = 24;
            public const int LengthWithoutSize = 20;
        }
    }
}

using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2C_MapFilename
    {
        public const int MaxLvzFiles = 16;
        private const int MaxFiles = MaxLvzFiles + 1; // +1 for map file

        // Type
        public byte Type;

        // Files
        private const int FileBytesLength = File.Length * MaxFiles;
        private fixed byte filesBytes[FileBytesLength];
        private Span<File> Files => MemoryMarshal.Cast<byte, File>(MemoryMarshal.CreateSpan(ref filesBytes[0], FileBytesLength));

        public S2C_MapFilename()
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
            file.FileName = fileName;
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
            file.FileName = fileName;
            file.Checksum = checksum;
            file.Size = size;

            return 1 + ((fileIndex + 1) * File.Length);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public unsafe struct File
        {
            // File Name
            private const int FileNameBytesLength = 16;
            private fixed byte fileNameBytes[FileNameBytesLength];
            public Span<byte> FileNameBytes => MemoryMarshal.CreateSpan(ref fileNameBytes[0], FileNameBytesLength);
            public string FileName
            {
                get => FileNameBytes.ReadNullTerminatedString();
                set => FileNameBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(FileNameBytesLength), false);
            }

            // Checksum
            private uint checksum;
            public uint Checksum
            {
                get => LittleEndianConverter.Convert(checksum);
                set => checksum = LittleEndianConverter.Convert(value);
            }

            // Size (continuum only)
            private uint size;
            public uint Size
            {
                get => LittleEndianConverter.Convert(size);
                set => size = LittleEndianConverter.Convert(value);
            }

            public const int Length = 24;
            public const int LengthWithoutSize = 20;
        }
    }
}

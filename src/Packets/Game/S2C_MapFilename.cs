using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_MapFilename
    {
        #region Constants

        /// <summary>
        /// The maximum # of lvz files a <see cref="S2C_MapFilename"/> packet can represent.
        /// </summary>
        public const int MaxLvzFiles = 16;

        /// <summary>
        /// The maximum # of files (lvl and lvz files) a <see cref="S2C_MapFilename"/> packet can represent.
        /// </summary>
        public const int MaxFiles = MaxLvzFiles + 1; // +1 for map file

        #endregion

        public byte Type;
        private FilesInlineArray Files;

        public S2C_MapFilename()
        {
            Type = (byte)S2CPacketType.MapFilename;
        }

        #region Methods

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
            file.FileName.Set(fileName);
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
            file.FileName.Set(fileName);
            file.Checksum = checksum;
            file.Size = size;

            return 1 + ((fileIndex + 1) * File.Length);
        }

        #endregion

        #region Types

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct File
        {
            #region Constants

            /// <summary>
            /// The # of bytes, including the size (Continuum).
            /// </summary>
            public const int Length = 24;

            /// <summary>
            /// The # of bytes, without the size (VIE).
            /// </summary>
            public const int LengthWithoutSize = 20;

            #endregion

            public FileNameInlineArray FileName;
            private uint checksum;
            private uint size; // Continuum only

            #region Helper Properties

            public uint Checksum
            {
                get => LittleEndianConverter.Convert(checksum);
                set => checksum = LittleEndianConverter.Convert(value);
            }

            public uint Size
            {
                get => LittleEndianConverter.Convert(size);
                set => size = LittleEndianConverter.Convert(value);
            }

            #endregion

            #region Inline Array Types

            [InlineArray(Length)]
            public struct FileNameInlineArray
            {
                public const int Length = 16;

                [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
                [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
                private byte _element0;

                public void Set(ReadOnlySpan<char> value)
                {
                    StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length), false);
                }
            }

            #endregion
        }

        [InlineArray(MaxFiles)]
        public struct FilesInlineArray
        {
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private File _element0;
        }

        #endregion
    }
}

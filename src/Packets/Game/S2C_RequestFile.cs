using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Packet that requests the client to send a file.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2C_RequestFile
    {
        public readonly byte Type;
        private fixed byte pathBytes[PathBytesLength];
        private fixed byte filenameBytes[FilenameBytesLength];

        #region Helpers

        public const int PathBytesLength = 256;
        public Span<byte> PathBytes => MemoryMarshal.CreateSpan(ref pathBytes[0], PathBytesLength);
        public void SetPath(ReadOnlySpan<char> value)
        {
            PathBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(PathBytesLength - 1));
        }

        public const int FilenameBytesLength = 16;
        public Span<byte> FilenameBytes => MemoryMarshal.CreateSpan(ref filenameBytes[0], FilenameBytesLength);
        public void SetFilename(ReadOnlySpan<char> value)
        {
            FilenameBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(FilenameBytesLength - 1));
        }

        #endregion

        public S2C_RequestFile(ReadOnlySpan<char> path, string filename)
        {
            Type = (byte)S2CPacketType.RequestForFile;
            SetPath(path);
            SetFilename(filename);
        }
    }
}

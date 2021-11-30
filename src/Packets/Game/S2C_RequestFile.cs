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

        private const int PathBytesLength = 256;
        private fixed byte pathBytes[PathBytesLength];
        public Span<byte> PathBytes => MemoryMarshal.CreateSpan(ref pathBytes[0], PathBytesLength);

        private const int FilenameBytesLength = 16;
        private fixed byte filenameBytes[FilenameBytesLength];
        public Span<byte> FilenameBytes => MemoryMarshal.CreateSpan(ref filenameBytes[0], FilenameBytesLength);

        public string Path
        {
            get { return PathBytes.ReadNullTerminatedString(); }
            set { PathBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(PathBytesLength - 1)); }
        }

        public string Filename
        {
            get { return FilenameBytes.ReadNullTerminatedString(); }
            set { FilenameBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(FilenameBytesLength - 1)); }
        }

        public S2C_RequestFile(string path, string filename)
        {
            Type = (byte)S2CPacketType.RequestForFile;
            Path = path;
            Filename = filename;
        }
    }
}

using SS.Utilities;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.S2C
{
    /// <summary>
    /// Packet that requests the client to send a file.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct RequestFilePacket
    {
        public readonly byte Type;

        private const int PathLength = 256;
        private fixed byte pathBytes[PathLength];
        public Span<byte> PathBytes => new(Unsafe.AsPointer(ref pathBytes[0]), PathLength);

        private const int FilenameLength = 16;
        private fixed byte filenameBytes[FilenameLength];
        public Span<byte> FilenameBytes => new(Unsafe.AsPointer(ref filenameBytes[0]), FilenameLength);

        public string Path
        {
            get { return PathBytes.ReadNullTerminatedString(); }
            set { PathBytes.WriteNullPaddedString(value); }
        }

        public string Filename
        {
            get { return FilenameBytes.ReadNullTerminatedString(); }
            set { FilenameBytes.WriteNullPaddedString(value); }
        }

        public RequestFilePacket(string path, string filename)
        {
            Type = (byte)S2CPacketType.RequestForFile;
            Path = path;
            Filename = filename;
        }
    }
}

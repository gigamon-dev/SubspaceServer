using SS.Utilities;
using System;

namespace SS.Core.Packets
{
    /// <summary>
    /// Helper for a packet sent by the server, requesting the client to send a file.
    /// </summary>
    public readonly ref struct RequestFilePacket
    {
        private readonly Span<byte> bytes;

        public const int Length =
            1 // Type
            + 256 // Path
            + 16; // Filename

        public RequestFilePacket(Span<byte> bytes)
        {
            this.bytes = bytes;
        }

        public void Initialize()
        {
            bytes[0] = (byte)S2CPacketType.RequestForFile;
        }

        public void Initialize(string path, string filename)
        {
            Initialize();

            Path = path;
            Filename = filename;
        }

        public string Path
        {
            get { return bytes.Slice(1, 256).ReadNullTerminatedASCII(); }
            set { bytes.Slice(1, 256).WriteNullPaddedASCII(value); }
        }

        public string Filename
        {
            get { return bytes.Slice(257, 16).ReadNullTerminatedASCII(); }
            set { bytes.Slice(257, 16).WriteNullPaddedASCII(value); }
        }
    }
}

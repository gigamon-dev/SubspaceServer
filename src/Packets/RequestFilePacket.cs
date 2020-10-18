using SS.Utilities;
using System;

namespace SS.Core.Packets
{
    /// <summary>
    /// Helper for a packet sent by the server, requesting the client to send a file.
    /// </summary>
    public readonly ref struct RequestFilePacket
    {
        static RequestFilePacket()
        {
            DataLocationBuilder builder = new DataLocationBuilder();
            typeLocation = builder.CreateByteDataLocation();
            pathLocation = builder.CreateDataLocation(256);
            filenameLocation = builder.CreateDataLocation(16);
            Length = builder.NumBytes;
        }

        private static readonly ByteDataLocation typeLocation;
        private static readonly DataLocation pathLocation;
        private static readonly DataLocation filenameLocation;
        public static readonly int Length;

        private readonly Span<byte> bytes;

        public RequestFilePacket(Span<byte> bytes)
        {
            if (bytes.Length < Length)
                throw new ArgumentException($"Length is too small to contain a {nameof(RequestFilePacket)}.", nameof(bytes));

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

        public byte Type
        {
            get { return typeLocation.GetValue(bytes); }
            set { typeLocation.SetValue(bytes, value); }
        }

        public string Path
        {
            get { return pathLocation.Slice(bytes).ReadNullTerminatedASCII(); }
            set { pathLocation.Slice(bytes).WriteNullPaddedASCII(value); }
        }

        public string Filename
        {
            get { return filenameLocation.Slice(bytes).ReadNullTerminatedASCII(); }
            set { filenameLocation.Slice(bytes).WriteNullPaddedASCII(value); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public readonly ref struct RequestFilePacketSpan
    {
        private readonly Span<byte> bytes;

        public RequestFilePacketSpan(Span<byte> bytes)
        {
            this.bytes = bytes;
        }

        public static int Length = 
            1 // Type
            + 256 // Path
            + 16; // Filename

        public void Initialize(string path, string filename)
        {
            bytes[0] = (byte)S2CPacketType.RequestForFile;

            Path = path;
            Filename = filename;
        }

        public string Path
        {
            get { return Encoding.ASCII.GetString(bytes.Slice(1, 256)); }
            set
            {
                Span<byte> span = bytes.Slice(1, 256);
                int bytesWritten = 0;

                if (!string.IsNullOrWhiteSpace(value))
                {
                    if (value.Length > 256)
                        throw new ArgumentException("Length cannot be longer than 256.");

                    bytesWritten = Encoding.ASCII.GetBytes(value, span);
                }

                if (bytesWritten < 256)
                    span.Slice(bytesWritten).Clear(); // null terminate, including all remaining bytes
            }
        }

        public string Filename
        {
            get { return Encoding.ASCII.GetString(bytes.Slice(257, 16)); }
            set
            {
                Span<byte> span = bytes.Slice(257, 16);
                int bytesWritten = 0;

                if (!string.IsNullOrWhiteSpace(value))
                {
                    if (value.Length > 16)
                        throw new ArgumentException("Length cannot be longer than 16.");

                    bytesWritten = Encoding.ASCII.GetBytes(value, span);
                }

                if (bytesWritten < 16)
                    span.Slice(bytesWritten).Clear(); // null terminate, including all remaining bytes
            }
        }

    }

    // packet sent by the server requesting the client to send a file.
    public readonly struct RequestFilePacket
    {
        static RequestFilePacket()
        {
            DataLocationBuilder builder = new DataLocationBuilder();
            type = builder.CreateByteDataLocation();
            path = builder.CreateDataLocation(256);
            filename = builder.CreateDataLocation(16);
            Length = builder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static readonly DataLocation path;
        private static readonly DataLocation filename;
        public static readonly int Length;

        private readonly byte[] data;

        public RequestFilePacket(byte[] data)
        {
            this.data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public byte Type
        {
            get { return type.GetValue(data); }
            private set { type.SetValue(data, value); }
        }

        public string Path
        {
            get
            {
                return Encoding.ASCII.GetString(data, path.ByteOffset, path.NumBytes);
            }

            set
            {
                int numCharacters = value.Length;
                if (numCharacters > path.NumBytes)
                    numCharacters = path.NumBytes;

                Encoding.ASCII.GetBytes(value, 0, numCharacters, data, path.ByteOffset);
            }
        }

        public string Filename
        {
            get
            {
                return Encoding.ASCII.GetString(data, filename.ByteOffset, filename.NumBytes);
            }

            set
            {
                int numCharacters = value.Length;
                if (numCharacters > filename.NumBytes)
                    numCharacters = filename.NumBytes;

                Encoding.ASCII.GetBytes(value, 0, numCharacters, data, filename.ByteOffset);
            }
        }

        public void Initialize()
        {
            Type = (byte)S2CPacketType.RequestForFile;
        }

        public void Initialize(string path, string filename)
        {
            Initialize();

            Path = path;
            Filename = filename;
        }
    }
}

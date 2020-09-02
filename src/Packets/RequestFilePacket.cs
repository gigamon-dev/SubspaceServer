using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    // packet sent by the server requesting the client to send a file.
    public struct RequestFilePacket
    {
        static RequestFilePacket()
        {
            DataLocationBuilder builder = new DataLocationBuilder();
            _typeDataLocation = builder.CreateByteDataLocation();
            _pathDataLocation = builder.CreateDataLocation(256);
            _filenameDataLocation = builder.CreateDataLocation(16);
            Length = builder.NumBytes;
        }

        private static ByteDataLocation _typeDataLocation;
        private static DataLocation _pathDataLocation;
        private static DataLocation _filenameDataLocation;
        public static readonly int Length;

        private byte[] _data;

        public RequestFilePacket(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            _data = data;
        }

        public byte Type
        {
            get { return _typeDataLocation.GetValue(_data); }
            private set { _typeDataLocation.SetValue(_data, value); }
        }

        public string Path
        {
            get
            {
                return Encoding.ASCII.GetString(_data, _pathDataLocation.ByteOffset, _pathDataLocation.NumBytes);
            }

            set
            {
                int numCharacters = value.Length;
                if (numCharacters > _pathDataLocation.NumBytes)
                    numCharacters = _pathDataLocation.NumBytes;

                Encoding.ASCII.GetBytes(value, 0, numCharacters, _data, _pathDataLocation.ByteOffset);
            }
        }

        public string Filename
        {
            get
            {
                return Encoding.ASCII.GetString(_data, _filenameDataLocation.ByteOffset, _filenameDataLocation.NumBytes);
            }

            set
            {
                int numCharacters = value.Length;
                if (numCharacters > _filenameDataLocation.NumBytes)
                    numCharacters = _filenameDataLocation.NumBytes;

                Encoding.ASCII.GetBytes(value, 0, numCharacters, _data, _filenameDataLocation.ByteOffset);
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

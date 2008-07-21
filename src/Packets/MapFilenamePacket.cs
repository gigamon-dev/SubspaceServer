using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct MapFilenamePacket
    {
        public const int MaxLvzFiles = 16;
        private const int MaxFiles = 16 + 1; // +1 for map file

        static MapFilenamePacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            typeLocation = locationBuilder.CreateDataLocation(1);

            filenameLocation = new DataLocation[MaxFiles];
            checksumLocation = new UInt32DataLocation[MaxFiles];
            sizeLocation = new UInt32DataLocation[MaxFiles];

            for (int x = 0; x < MaxFiles; x++)
            {
                filenameLocation[x] = locationBuilder.CreateDataLocation(16);
                checksumLocation[x] = locationBuilder.CreateDataLocation(4);
                sizeLocation[x] = locationBuilder.CreateDataLocation(4); // cont only
            }
        }

        private static readonly ByteDataLocation typeLocation;
        private static readonly DataLocation[] filenameLocation;
        private static readonly UInt32DataLocation[] checksumLocation;
        private static readonly UInt32DataLocation[] sizeLocation;

        private readonly byte[] data;

        public MapFilenamePacket(byte[] data)
        {
            this.data = data;
        }

        public void Initialize()
        {
            Type = (byte)Packets.S2CPacketType.MapFilename;
        }

        public byte Type
        {
            get { return typeLocation.GetValue(data); }
            private set { typeLocation.SetValue(data, value); }
        }

        public int SetFileInfo(int fileIndex, string filename, uint checksum, uint? size)
        {
            if (fileIndex >= MaxFiles)
                throw new ArgumentOutOfRangeException("fileIndex", ">= " + MaxFiles);

            int charCount = filenameLocation[fileIndex].NumBytes;
            if (filename.Length < charCount)
                charCount = filename.Length;

            Encoding.ASCII.GetBytes(filename, 0, charCount, data, filenameLocation[fileIndex].ByteOffset);
            checksumLocation[fileIndex].SetValue(data, checksum);

            if (size != null)
            {
                sizeLocation[fileIndex].SetValue(data, size.Value);
                return sizeLocation[fileIndex].ByteOffset + sizeLocation[fileIndex].NumBytes;
            }
            else
            {
                return checksumLocation[fileIndex].ByteOffset + checksumLocation[fileIndex].NumBytes;
            }
        }
    }
}

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
            typeLocation = locationBuilder.CreateDataLocation(8);

            filenameLocation = new DataLocation[MaxFiles];
            checksumLocation = new DataLocation[MaxFiles];
            sizeLocation = new DataLocation[MaxFiles];

            for (int x = 0; x < MaxFiles; x++)
            {
                filenameLocation[x] = locationBuilder.CreateDataLocation(8 * 16);
                checksumLocation[x] = locationBuilder.CreateDataLocation(32);
                sizeLocation[x] = locationBuilder.CreateDataLocation(32); // cont only
            }
        }

        private static readonly DataLocation typeLocation;
        private static readonly DataLocation[] filenameLocation;
        private static readonly DataLocation[] checksumLocation;
        private static readonly DataLocation[] sizeLocation;

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
            get { return ExtendedBitConverter.ToByte(data, typeLocation.ByteOffset, typeLocation.BitOffset); }
            private set { ExtendedBitConverter.WriteByteBits(value, data, typeLocation.ByteOffset, typeLocation.BitOffset, typeLocation.NumBits); }
        }

        public int SetFileInfo(int fileIndex, string filename, uint checksum, uint? size)
        {
            if (fileIndex >= MaxFiles)
                throw new ArgumentOutOfRangeException("fileIndex", ">= " + MaxFiles);

            int charCount = filenameLocation[fileIndex].NumBits / 8;
            if (filename.Length < charCount)
                charCount = filename.Length;

            Encoding.ASCII.GetBytes(filename, 0, charCount, data, filenameLocation[fileIndex].ByteOffset);
            ExtendedBitConverter.WriteUInt32Bits(checksum, data, checksumLocation[fileIndex].ByteOffset, checksumLocation[fileIndex].BitOffset, checksumLocation[fileIndex].NumBits);

            if (size != null)
            {
                ExtendedBitConverter.WriteUInt32Bits(checksum, data, sizeLocation[fileIndex].ByteOffset, sizeLocation[fileIndex].BitOffset, sizeLocation[fileIndex].NumBits);
                return sizeLocation[fileIndex].ByteOffset + (sizeLocation[fileIndex].NumBits / 8);
            }
            else
            {
                return checksumLocation[fileIndex].ByteOffset + (checksumLocation[fileIndex].NumBits / 8);
            }
        }
    }
}

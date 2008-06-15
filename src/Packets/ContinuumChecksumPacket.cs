using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct ContinuumChecksumPacket
    {
        static ContinuumChecksumPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateDataLocation(8);
            contversion = locationBuilder.CreateDataLocation(16);
            checksum = locationBuilder.CreateDataLocation(32);
            Length = locationBuilder.NumBytes;
        }

        private static readonly DataLocation type;
        private static readonly DataLocation contversion;
        private static readonly DataLocation checksum;
        public static readonly int Length;

        private readonly byte[] data;

        public ContinuumChecksumPacket(byte[] data)
        {
            this.data = data;
        }

        public byte Type
        {
            get { return ExtendedBitConverter.ToByte(data, type.ByteOffset, type.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, type.ByteOffset, type.BitOffset, type.NumBits); }
        }

        public ushort ContVersion
        {
            get { return ExtendedBitConverter.ToUInt16(data, contversion.ByteOffset, contversion.BitOffset); }
            set { ExtendedBitConverter.WriteUInt16Bits(value, data, contversion.ByteOffset, contversion.BitOffset, contversion.NumBits); }
        }

        public uint Checksum
        {
            get { return ExtendedBitConverter.ToUInt32(data, checksum.ByteOffset, checksum.BitOffset); }
            set { ExtendedBitConverter.WriteUInt32Bits(value, data, checksum.ByteOffset, checksum.BitOffset, checksum.NumBits); }
        }
    }
}

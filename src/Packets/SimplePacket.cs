using System;
using System.Collections.Generic;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct SimplePacket
    {
        // static constructor to initialize packet's info
        static SimplePacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateDataLocation(8);
            d1 = locationBuilder.CreateDataLocation(16);
            d2 = locationBuilder.CreateDataLocation(16);
            d3 = locationBuilder.CreateDataLocation(16);
            d4 = locationBuilder.CreateDataLocation(16);
            d5 = locationBuilder.CreateDataLocation(16);
            NumBytes = locationBuilder.NumBytes;
        }

        // static data members that tell the location of each field in the byte array of a packet
        private static readonly DataLocation type;
        private static readonly DataLocation d1;
        private static readonly DataLocation d2;
        private static readonly DataLocation d3;
        private static readonly DataLocation d4;
        private static readonly DataLocation d5;
        public static readonly int NumBytes;

        // data members
        private readonly byte[] data;

        public SimplePacket(byte[] data)
        {
            this.data = data;
        }

        public byte Type
        {
            get { return ExtendedBitConverter.ToByte(data, type.ByteOffset, type.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, type.ByteOffset, type.BitOffset, type.NumBits); }
        }

        public short D1
        {
            get { return ExtendedBitConverter.ToInt16(data, d1.ByteOffset, d1.BitOffset); }
            set { ExtendedBitConverter.WriteInt16Bits(value, data, d1.ByteOffset, d1.BitOffset, d1.NumBits); }
        }

        public short D2
        {
            get { return ExtendedBitConverter.ToInt16(data, d2.ByteOffset, d2.BitOffset); }
            set { ExtendedBitConverter.WriteInt16Bits(value, data, d2.ByteOffset, d2.BitOffset, d2.NumBits); }
        }

        public short D3
        {
            get { return ExtendedBitConverter.ToInt16(data, d3.ByteOffset, d3.BitOffset); }
            set { ExtendedBitConverter.WriteInt16Bits(value, data, d3.ByteOffset, d3.BitOffset, d3.NumBits); }
        }

        public short D4
        {
            get { return ExtendedBitConverter.ToInt16(data, d4.ByteOffset, d4.BitOffset); }
            set { ExtendedBitConverter.WriteInt16Bits(value, data, d4.ByteOffset, d4.BitOffset, d4.NumBits); }
        }

        public short D5
        {
            get { return ExtendedBitConverter.ToInt16(data, d5.ByteOffset, d5.BitOffset); }
            set { ExtendedBitConverter.WriteInt16Bits(value, data, d5.ByteOffset, d5.BitOffset, d5.NumBits); }
        }
    }

    /*
    public class SimplePacketA
    {
    }
    */
}

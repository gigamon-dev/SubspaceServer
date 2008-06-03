using System;
using System.Collections.Generic;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct DataLocation
    {
        public readonly int ByteOffset;
        public readonly int BitOffset;
        public readonly int NumBits;

        public DataLocation(int byteOffset, int bitOffset, int numBits)
        {
            ByteOffset = byteOffset;
            BitOffset = bitOffset;
            NumBits = numBits;
        }
    }

    public class DataLocationBuilder
    {
        private int _nextByte = 0;
        private int _nextBit = 0;

        public DataLocation CreateDataLocation(int numBits)
        {
            if (numBits < 1)
                throw new ArgumentOutOfRangeException("must be >= 1", "numBits");

            try
            {
                return new DataLocation(_nextByte, _nextBit, numBits);
            }
            finally
            {
                _nextBit += numBits;
                _nextByte += (int)(_nextBit / 8);
                _nextBit %= 8;
            }
        }

        public int NumBytes
        {
            get
            {
                return (_nextBit > 0) ? _nextByte + 1 : _nextByte;
            }
        }
    }

    public struct SimplePacketTest
    {
        // static constructor to initialize packet's info
        static SimplePacketTest()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateDataLocation(7);
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

        public SimplePacketTest(byte[] data)
        {
            this.data = data;
        }

        public byte Type
        {
            get { return ExtendedBitConverter.ToByte(data, type.ByteOffset, type.BitOffset); }
        }

        public short D1
        {
            get { return ExtendedBitConverter.ToInt16(data, d1.ByteOffset, d1.BitOffset); }
        }

        public short D2
        {
            get { return ExtendedBitConverter.ToInt16(data, d2.ByteOffset, d2.BitOffset); }
        }

        public short D3
        {
            get { return ExtendedBitConverter.ToInt16(data, d3.ByteOffset, d3.BitOffset); }
        }

        public short D4
        {
            get { return ExtendedBitConverter.ToInt16(data, d4.ByteOffset, d4.BitOffset); }
        }

        public short D5
        {
            get { return ExtendedBitConverter.ToInt16(data, d5.ByteOffset, d5.BitOffset); }
        }
    }

    public class SimplePacket
    {
        public byte type;
        public short d1, d2, d3, d4, d5;
    }
    /*
    public class SimplePacketA
    {
    }
    */
}

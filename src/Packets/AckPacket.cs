using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct AckPacket
    {
        // static constructor to initialize packet's info
        static AckPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            t1 = locationBuilder.CreateDataLocation(8);
            t2 = locationBuilder.CreateDataLocation(8);
            seqNum = locationBuilder.CreateDataLocation(32);
            length = locationBuilder.NumBytes;
        }

        // static data members that tell the location of each field in the byte array of a packet
        private static readonly DataLocation t1;
        private static readonly DataLocation t2;
        private static readonly DataLocation seqNum;
        private static readonly int length;

        // data members
        private readonly byte[] data;

        public AckPacket(byte[] data)
        {
            this.data = data;
        }

        public byte T1
        {
            get { return ExtendedBitConverter.ToByte(data, t1.ByteOffset, t1.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, t1.ByteOffset, t1.BitOffset, t1.NumBits); }
        }

        public byte T2
        {
            get { return ExtendedBitConverter.ToByte(data, t2.ByteOffset, t2.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, t2.ByteOffset, t2.BitOffset, t2.NumBits); }
        }

        public int SeqNum
        {
            get { return ExtendedBitConverter.ToInt32(data, seqNum.ByteOffset, seqNum.BitOffset); }
            set { ExtendedBitConverter.WriteInt32Bits(value, data, seqNum.ByteOffset, seqNum.BitOffset, seqNum.NumBits); }
        }

        public static int Length
        {
            get { return length; }
        }
    }
}

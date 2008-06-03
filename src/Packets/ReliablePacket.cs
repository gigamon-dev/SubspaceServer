using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct ReliablePacket
    {
        // static constructor to initialize packet's info
        static ReliablePacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            t1 = locationBuilder.CreateDataLocation(8);
            t2 = locationBuilder.CreateDataLocation(8);
            seqNum = locationBuilder.CreateDataLocation(32);

        }

        // static data members that tell the location of each field in the byte array of a packet
        private static readonly DataLocation t1;
        private static readonly DataLocation t2;
        private static readonly DataLocation seqNum;

        // data members
        private readonly byte[] data;

        public ReliablePacket(byte[] data)
        {
            this.data = data;
        }

        public byte T1
        {
            get { return ExtendedBitConverter.ToByte(data, t1.ByteOffset, t1.BitOffset); }
        }

        public byte T2
        {
            get { return ExtendedBitConverter.ToByte(data, t2.ByteOffset, t2.BitOffset); }
        }

        public int SeqNum
        {
            get { return ExtendedBitConverter.ToInt32(data, seqNum.ByteOffset, seqNum.BitOffset); }
        }
    }
}

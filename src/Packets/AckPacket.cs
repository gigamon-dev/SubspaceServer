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
            t1 = locationBuilder.CreateDataLocation(1);
            t2 = locationBuilder.CreateDataLocation(1);
            seqNum = locationBuilder.CreateDataLocation(4);
            Length = locationBuilder.NumBytes;
        }

        // static data members that tell the location of each field in the byte array of a packet
        private static readonly ByteDataLocation t1;
        private static readonly ByteDataLocation t2;
        private static readonly Int32DataLocation seqNum;
        public static readonly int Length;

        // data members
        private readonly byte[] data;

        public AckPacket(byte[] data)
        {
            this.data = data;
        }

        public byte T1
        {
            get { return t1.GetValue(data); }
            set { t1.SetValue(data, value); }
        }

        public byte T2
        {
            get { return t2.GetValue(data); }
            set { t2.SetValue(data, value); }
        }

        public int SeqNum
        {
            get { return seqNum.GetValue(data); }
            set { seqNum.SetValue(data, value); }
        }
    }
}

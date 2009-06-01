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
            t1 = locationBuilder.CreateByteDataLocation();
            t2 = locationBuilder.CreateByteDataLocation();
            seqNum = locationBuilder.CreateInt32DataLocation();
            dataStartIndex = locationBuilder.CreateDataLocation(1).ByteOffset;
        }

        // static data members that tell the location of each field in the byte array of a packet
        private static readonly ByteDataLocation t1;
        private static readonly ByteDataLocation t2;
        private static readonly Int32DataLocation seqNum;
        private static readonly int dataStartIndex;

        // data members
        private readonly byte[] data;

        public ReliablePacket(byte[] data)
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

        public void SetData(byte[] d, int len)
        {
            Array.Copy(d, 0, data, dataStartIndex, len);
        }

        public void SetData(ArraySegment<byte> d)
        {
            Array.Copy(d.Array, d.Offset, data, dataStartIndex, d.Count);
        }
        /*
        public static int DataStartIndex
        {
            get { return dataStartIndex; }
        }*/

        public ArraySegment<byte> GetData(int len)
        {
            return new ArraySegment<byte>(data, dataStartIndex, len);
        }
    }
}

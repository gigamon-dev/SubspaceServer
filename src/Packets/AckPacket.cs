using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public readonly ref struct AckPacket
    {
        // static constructor to initialize packet's info
        static AckPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            t1 = locationBuilder.CreateByteDataLocation();
            t2 = locationBuilder.CreateByteDataLocation();
            seqNum = locationBuilder.CreateInt32DataLocation();
            Length = locationBuilder.NumBytes;
        }

        // static data members that tell the location of each field in the byte array of a packet
        private static readonly ByteDataLocation t1;
        private static readonly ByteDataLocation t2;
        private static readonly Int32DataLocation seqNum;
        public static readonly int Length;

        // data members
        private readonly Span<byte> bytes;

        public AckPacket(Span<byte> bytes)
        {
            if (bytes.Length < Length)
                throw new ArgumentException($"Length is too small to contain a {nameof(AckPacket)}.", nameof(bytes));

            this.bytes = bytes;
        }

        public void Initialize(int seqNum)
        {
            T1 = 0x00;
            T2 = 0x04;
            SeqNum = seqNum;
        }

        public byte T1
        {
            get { return t1.GetValue(bytes); }
            set { t1.SetValue(bytes, value); }
        }

        public byte T2
        {
            get { return t2.GetValue(bytes); }
            set { t2.SetValue(bytes, value); }
        }

        public int SeqNum
        {
            get { return seqNum.GetValue(bytes); }
            set { seqNum.SetValue(bytes, value); }
        }
    }
}

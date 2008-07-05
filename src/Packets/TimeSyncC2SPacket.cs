using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct TimeSyncC2SPacket
    {
        // static constructor to initialize packet's info
        static TimeSyncC2SPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            t1 = locationBuilder.CreateDataLocation(8);
            t2 = locationBuilder.CreateDataLocation(8);
            time = locationBuilder.CreateDataLocation(32);
            pktsent = locationBuilder.CreateDataLocation(32);
            pktrecvd = locationBuilder.CreateDataLocation(32);
            length = locationBuilder.NumBytes;
        }

        // static data members that tell the location of each field in the byte array of a packet
        private static readonly DataLocation t1;
        private static readonly DataLocation t2;
        private static readonly DataLocation time;
        private static readonly DataLocation pktsent;
        private static readonly DataLocation pktrecvd;
        private static readonly int length;

        // data members
        private readonly byte[] data;

        public TimeSyncC2SPacket(byte[] data)
        {
            this.data = data;
        }

        public byte T1
        {
            get { return ExtendedBitConverter.ToByte(data, t1.ByteOffset, t1.BitOffset); }
            //set { ExtendedBitConverter.WriteByteBits(value, data, t1.ByteOffset, t1.BitOffset, t1.NumBits); }
        }

        public byte T2
        {
            get { return ExtendedBitConverter.ToByte(data, t2.ByteOffset, t2.BitOffset); }
            //set { ExtendedBitConverter.WriteByteBits(value, data, t2.ByteOffset, t2.BitOffset, t2.NumBits); }
        }

        public uint Time
        {
            get { return ExtendedBitConverter.ToUInt32(data, time.ByteOffset, time.BitOffset); }
            //set { ExtendedBitConverter.WriteInt32Bits(value, data, time.ByteOffset, time.BitOffset, time.NumBits); }
        }

        public uint PktSent
        {
            get { return ExtendedBitConverter.ToUInt32(data, pktsent.ByteOffset, pktsent.BitOffset); }
            //set { ExtendedBitConverter.WriteInt32Bits(value, data, pktsent.ByteOffset, pktsent.BitOffset, pktsent.NumBits); }
        }

        public uint PktRecvd
        {
            get { return ExtendedBitConverter.ToUInt32(data, pktrecvd.ByteOffset, pktrecvd.BitOffset); }
            //set { ExtendedBitConverter.WriteInt32Bits(value, data, pktrecvd.ByteOffset, pktrecvd.BitOffset, pktrecvd.NumBits); }
        }

        public static int Length
        {
            get { return length; }
        }
    }
}

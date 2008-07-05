using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct TimeSyncS2CPacket
    {
        // static constructor to initialize packet's info
        static TimeSyncS2CPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            t1 = locationBuilder.CreateDataLocation(8);
            t2 = locationBuilder.CreateDataLocation(8);
            clienttime = locationBuilder.CreateDataLocation(32);
            servertime = locationBuilder.CreateDataLocation(32);
            length = locationBuilder.NumBytes;
        }

        // static data members that tell the location of each field in the byte array of a packet
        private static readonly DataLocation t1;
        private static readonly DataLocation t2;
        private static readonly DataLocation clienttime;
        private static readonly DataLocation servertime;
        private static readonly int length;

        // data members
        private readonly byte[] data;

        public TimeSyncS2CPacket(byte[] data)
        {
            this.data = data;
        }

        public void Initialize()
        {
            T1 = 0x00;
            T2 = 0x06;
        }

        public byte T1
        {
            //get { return ExtendedBitConverter.ToByte(data, t1.ByteOffset, t1.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, t1.ByteOffset, t1.BitOffset, t1.NumBits); }
        }

        public byte T2
        {
            //get { return ExtendedBitConverter.ToByte(data, t2.ByteOffset, t2.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, t2.ByteOffset, t2.BitOffset, t2.NumBits); }
        }

        public uint ClientTime
        {
            //get { return ExtendedBitConverter.ToInt32(data, clienttime.ByteOffset, clienttime.BitOffset); }
            set { ExtendedBitConverter.WriteUInt32Bits(value, data, clienttime.ByteOffset, clienttime.BitOffset, clienttime.NumBits); }
        }

        public uint ServerTime
        {
            //get { return ExtendedBitConverter.ToInt32(data, servertime.ByteOffset, servertime.BitOffset); }
            set { ExtendedBitConverter.WriteUInt32Bits(value, data, servertime.ByteOffset, servertime.BitOffset, servertime.NumBits); }
        }

        public static int Length
        {
            get { return length; }
        }
    }
}

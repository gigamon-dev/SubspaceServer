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
            t1 = locationBuilder.CreateDataLocation(1);
            t2 = locationBuilder.CreateDataLocation(1);
            time = locationBuilder.CreateDataLocation(4);
            pktsent = locationBuilder.CreateDataLocation(4);
            pktrecvd = locationBuilder.CreateDataLocation(4);
            length = locationBuilder.NumBytes;
        }

        // static data members that tell the location of each field in the byte array of a packet
        private static readonly ByteDataLocation t1;
        private static readonly ByteDataLocation t2;
        private static readonly UInt32DataLocation time;
        private static readonly UInt32DataLocation pktsent;
        private static readonly UInt32DataLocation pktrecvd;
        private static readonly int length;

        // data members
        private readonly byte[] data;

        public TimeSyncC2SPacket(byte[] data)
        {
            this.data = data;
        }

        public byte T1
        {
            get { return t1.GetValue(data); }
        }

        public byte T2
        {
            get { return t2.GetValue(data); }
        }

        public uint Time
        {
            get { return time.GetValue(data); }
        }

        public uint PktSent
        {
            get { return pktsent.GetValue(data); }
        }

        public uint PktRecvd
        {
            get { return pktrecvd.GetValue(data); }
        }

        public static int Length
        {
            get { return length; }
        }
    }
}

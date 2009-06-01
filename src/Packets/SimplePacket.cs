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
            type = locationBuilder.CreateByteDataLocation();
            d1 = locationBuilder.CreateInt16DataLocation();
            d2 = locationBuilder.CreateInt16DataLocation();
            d3 = locationBuilder.CreateInt16DataLocation();
            d4 = locationBuilder.CreateInt16DataLocation();
            d5 = locationBuilder.CreateInt16DataLocation();
            NumBytes = locationBuilder.NumBytes;
        }

        // static data members that tell the location of each field in the byte array of a packet
        private static readonly ByteDataLocation type;
        private static readonly Int16DataLocation d1;
        private static readonly Int16DataLocation d2;
        private static readonly Int16DataLocation d3;
        private static readonly Int16DataLocation d4;
        private static readonly Int16DataLocation d5;
        public static readonly int NumBytes;

        // data members
        private readonly byte[] data;

        public SimplePacket(byte[] data)
        {
            this.data = data;
        }

        public byte Type
        {
            get { return type.GetValue(data); }
            set { type.SetValue(data, value); }
        }

        public short D1
        {
            get { return d1.GetValue(data); }
            set { d1.SetValue(data, value); }
        }

        public short D2
        {
            get { return d2.GetValue(data); }
            set { d2.SetValue(data, value); }
        }

        public short D3
        {
            get { return d3.GetValue(data); }
            set { d3.SetValue(data, value); }
        }

        public short D4
        {
            get { return d4.GetValue(data); }
            set { d4.SetValue(data, value); }
        }

        public short D5
        {
            get { return d5.GetValue(data); }
            set { d5.SetValue(data, value); }
        }
    }

    /*
    public class SimplePacketA
    {
    }
    */
}

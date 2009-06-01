using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct ShipChangePacket
    {
        static ShipChangePacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateByteDataLocation();
            shiptype = locationBuilder.CreateSByteDataLocation();
            pid = locationBuilder.CreateInt16DataLocation();
            freq = locationBuilder.CreateInt16DataLocation();
            Length = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static readonly SByteDataLocation shiptype;
        private static readonly Int16DataLocation pid;
        private static readonly Int16DataLocation freq;
        public static readonly int Length;

        private readonly byte[] data;

        public ShipChangePacket(byte[] data)
        {
            this.data = data;
        }

        public byte Type
        {
            get { return type.GetValue(data); }
            set { type.SetValue(data, value); }
        }

        public sbyte ShipType
        {
            get { return shiptype.GetValue(data); }
            set { shiptype.SetValue(data, value); }
        }

        public short Pid
        {
            get { return pid.GetValue(data); }
            set { pid.SetValue(data, value); }
        }

        public short Freq
        {
            get { return freq.GetValue(data); }
            set { freq.SetValue(data, value); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public readonly struct BrickPacket
    {
        static BrickPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateByteDataLocation();
            x1 = locationBuilder.CreateInt16DataLocation();
            y1 = locationBuilder.CreateInt16DataLocation();
            x2 = locationBuilder.CreateInt16DataLocation();
            y2 = locationBuilder.CreateInt16DataLocation();
            freq = locationBuilder.CreateInt16DataLocation();
            brickid = locationBuilder.CreateUInt16DataLocation();
            starttime = locationBuilder.CreateUInt32DataLocation();
            Length = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type; // S2C: 0x21
        private static readonly Int16DataLocation x1;
        private static readonly Int16DataLocation y1;
        private static readonly Int16DataLocation x2;
        private static readonly Int16DataLocation y2;
        private static readonly Int16DataLocation freq;
        private static readonly UInt16DataLocation brickid;
        private static readonly UInt32DataLocation starttime;
        public static readonly int Length;

        private readonly byte[] data;

        public BrickPacket(byte[] data)
        {
            this.data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public byte Type
        {
            get { return type.GetValue(data); }
            set { type.SetValue(data, value); }
        }

        public short X1
        {
            get { return x1.GetValue(data); }
            set { x1.SetValue(data, value); }
        }

        public short X2
        {
            get { return x2.GetValue(data); }
            set { x2.SetValue(data, value); }
        }

        public short Y1
        {
            get { return y1.GetValue(data); }
            set { y1.SetValue(data, value); }
        }

        public short Y2
        {
            get { return y2.GetValue(data); }
            set { y2.SetValue(data, value); }
        }

        public short Freq
        {
            get { return freq.GetValue(data); }
            set { freq.SetValue(data, value); }
        }

        public ushort BrickId
        {
            get { return brickid.GetValue(data); }
            set { brickid.SetValue(data, value); }
        }

        public uint StartTime
        {
            get { return starttime.GetValue(data); }
            set { starttime.SetValue(data, value); }
        }
    }
}

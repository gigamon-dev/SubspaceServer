using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public enum Prize
    {
        Recharge = 1,
        Energy,
        Rotation,
        Stealth,
        Cloak,
        XRadar,
        Warp,
        Gun,
        Bomb,
        Bounce,
        Thrust,
        Speed,
        FullCharge,
        Shutdown,
        Multifire,
        Prox,
        Super,
        Shield,
        Shrap,
        Antiwarp,
        Repel,
        Burst,
        Decoy,
        Thor,
        Multiprize,
        Brick,
        Rocket,
        Portal
    }

    public readonly struct GreenPacket
    {
        static GreenPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateByteDataLocation();
            time = locationBuilder.CreateUInt32DataLocation();
            x = locationBuilder.CreateInt16DataLocation();
            y = locationBuilder.CreateInt16DataLocation();
            green = locationBuilder.CreateInt16DataLocation();
            C2SLength = locationBuilder.NumBytes;
            pid = locationBuilder.CreateInt16DataLocation();
            S2CLength = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static readonly UInt32DataLocation time;
        private static readonly Int16DataLocation x;
        private static readonly Int16DataLocation y;
        private static readonly Int16DataLocation green;
        private static readonly Int16DataLocation pid;
        public static readonly int C2SLength;
        public static readonly int S2CLength;

        private readonly byte[] data;

        public GreenPacket(byte[] data)
        {
            this.data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public byte Type
        {
            get { return type.GetValue(data); }
            set { type.SetValue(data, value); }
        }

        public uint Time
        {
            get { return time.GetValue(data); }
            set { time.SetValue(data, value); }
        }

        public short X
        {
            get { return x.GetValue(data); }
            set { x.SetValue(data, value); }
        }

        public short Y
        {
            get { return y.GetValue(data); }
            set { y.SetValue(data, value); }
        }

        public Prize Green
        {
            get { return (Prize)green.GetValue(data); }
            set { green.SetValue(data, (short)value); }
        }

        public short Pid
        {
            get { return pid.GetValue(data); }
            set { pid.SetValue(data, value); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct KillPacket
    {
        static KillPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = (ByteDataLocation)locationBuilder.CreateDataLocation(8);
            green = (ByteDataLocation)locationBuilder.CreateDataLocation(8);
            killer = (Int16DataLocation)locationBuilder.CreateDataLocation(16);
            killed = (Int16DataLocation)locationBuilder.CreateDataLocation(16);
            bounty = (Int16DataLocation)locationBuilder.CreateDataLocation(16);
            flags = (Int16DataLocation)locationBuilder.CreateDataLocation(16);
            Length = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static readonly ByteDataLocation green;
        private static readonly Int16DataLocation killer;
        private static readonly Int16DataLocation killed;
        private static readonly Int16DataLocation bounty;
        private static readonly Int16DataLocation flags;
        public static readonly int Length;

        private readonly byte[] data;

        public KillPacket(byte[] data)
        {
            this.data = data;
        }

        public byte Type
        {
            get { return type.GetValue(data); }
            set { type.SetValue(data, value); }
        }

        public Prize Green
        {
            get { return (Prize)green.GetValue(data); }
            set { green.SetValue(data, (byte)value); }
        }

        public short Killer
        {
            get { return killer.GetValue(data); }
            set { killer.SetValue(data, value); }
        }

        public short Killed
        {
            get { return killed.GetValue(data); }
            set { killed.SetValue(data, value); }
        }

        public short Bounty
        {
            get { return bounty.GetValue(data); }
            set { bounty.SetValue(data, value); }
        }

        public short Flags
        {
            get { return flags.GetValue(data); }
            set { flags.SetValue(data, value); }
        }
    }
}

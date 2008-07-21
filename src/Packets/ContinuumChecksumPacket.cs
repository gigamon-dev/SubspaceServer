using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct ContinuumChecksumPacket
    {
        static ContinuumChecksumPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateDataLocation(1);
            contversion = locationBuilder.CreateDataLocation(2);
            checksum = locationBuilder.CreateDataLocation(4);
            Length = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static readonly UInt16DataLocation contversion;
        private static readonly UInt32DataLocation checksum;
        public static readonly int Length;

        private readonly byte[] data;

        public ContinuumChecksumPacket(byte[] data)
        {
            this.data = data;
        }

        public byte Type
        {
            get { return type.GetValue(data); }
            set { type.SetValue(data, value); }
        }

        public ushort ContVersion
        {
            get { return contversion.GetValue(data); }
            set { contversion.SetValue(data, value); }
        }

        public uint Checksum
        {
            get { return checksum.GetValue(data); }
            set { checksum.SetValue(data, value); }
        }
    }
}

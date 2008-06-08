using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    /// <summary>
    /// player login packet
    /// </summary>
    public struct LoginPacket
    {
        static LoginPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateDataLocation(8);
            flags = locationBuilder.CreateDataLocation(8);
            name = locationBuilder.CreateDataLocation(8 * 32);
            macid = locationBuilder.CreateDataLocation(32);
            blah = locationBuilder.CreateDataLocation(8);
            timezonebias = locationBuilder.CreateDataLocation(16);
            unk1 = locationBuilder.CreateDataLocation(16);
            cversion = locationBuilder.CreateDataLocation(16);
            field444 = locationBuilder.CreateDataLocation(32);
            field555 = locationBuilder.CreateDataLocation(32);
            blah2 = locationBuilder.CreateDataLocation(8 * 12);
            LengthVIE = locationBuilder.NumBytes;
            contid = locationBuilder.CreateDataLocation(64);
            LengthContinuum = locationBuilder.NumBytes;
        }

        private static readonly DataLocation type;
        private static readonly DataLocation flags;
        private static readonly DataLocation name;
        private static readonly DataLocation password;
        private static readonly DataLocation macid;
        private static readonly DataLocation blah;
        private static readonly DataLocation timezonebias;
        private static readonly DataLocation unk1;
        private static readonly DataLocation cversion;
        private static readonly DataLocation field444;
        private static readonly DataLocation field555;
        private static readonly DataLocation d2;
        private static readonly DataLocation blah2;
        private static readonly DataLocation contid; // cont only
        public static readonly int LengthVIE;
        public static readonly int LengthContinuum;

        private readonly byte[] data;

        public LoginPacket(byte[] data)
        {
            this.data = data;
        }

        public string Name
        {
            get { return Encoding.ASCII.GetString(data, name.ByteOffset, name.NumBits / 8); }
        }

        public short CVersion
        {
            get { return ExtendedBitConverter.ToInt16(data, cversion.ByteOffset, cversion.BitOffset); }
        }
    }
}

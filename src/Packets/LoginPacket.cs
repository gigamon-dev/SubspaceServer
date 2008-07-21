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
            type = locationBuilder.CreateDataLocation(1);
            flags = locationBuilder.CreateDataLocation(1);
            name = locationBuilder.CreateDataLocation(32);
            password = locationBuilder.CreateDataLocation(32);
            macid = locationBuilder.CreateDataLocation(4);
            blah = locationBuilder.CreateDataLocation(1);
            timezonebias = locationBuilder.CreateDataLocation(2);
            unk1 = locationBuilder.CreateDataLocation(2);
            cversion = locationBuilder.CreateDataLocation(2);
            field444 = locationBuilder.CreateDataLocation(4);
            field555 = locationBuilder.CreateDataLocation(4);
            d2 = locationBuilder.CreateDataLocation(4);
            blah2 = locationBuilder.CreateDataLocation(12);
            LengthVIE = locationBuilder.NumBytes;
            contid = locationBuilder.CreateDataLocation(64);
            LengthContinuum = locationBuilder.NumBytes;
        }

        private static readonly DataLocation type;
        private static readonly DataLocation flags;
        private static readonly DataLocation name;
        private static readonly DataLocation password;
        private static readonly UInt32DataLocation macid;
        private static readonly DataLocation blah;
        private static readonly DataLocation timezonebias;
        private static readonly DataLocation unk1;
        private static readonly Int16DataLocation cversion;
        private static readonly DataLocation field444;
        private static readonly DataLocation field555;
        private static readonly UInt32DataLocation d2;
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
            get
            {
                string str = Encoding.ASCII.GetString(data, name.ByteOffset, name.NumBytes);
                int index = str.IndexOf('\0');
                if (index != -1)
                {
                    return str.Substring(0, index);
                }
                return str;
            }
        }

        public uint MacId
        {
            get { return macid.GetValue(data); }
        }

        public short CVersion
        {
            get { return cversion.GetValue(data); }            
        }

        public uint D2
        {
            get { return d2.GetValue(data); }
        }

        public static int Copy(LoginPacket sourcePacket, LoginPacket destinationPacket)
        {
            int minDataLength = Math.Min(sourcePacket.data.Length, destinationPacket.data.Length);
            Array.Copy(sourcePacket.data, destinationPacket.data, minDataLength);

            return minDataLength;
        }
    }
}

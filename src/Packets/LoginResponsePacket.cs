using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct LoginResponsePacket
    {
        static LoginResponsePacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateDataLocation(1);
            code = locationBuilder.CreateDataLocation(1);
            serverversion = locationBuilder.CreateDataLocation(4);
            isvip = locationBuilder.CreateDataLocation(1);
            blah = locationBuilder.CreateDataLocation(3);
            exechecksum = locationBuilder.CreateDataLocation(4);
            blah2 = locationBuilder.CreateDataLocation(5);
            demodata = locationBuilder.CreateDataLocation(1);
            codechecksum = locationBuilder.CreateDataLocation(4);
            newschecksum = locationBuilder.CreateDataLocation(4);
            blah4 = locationBuilder.CreateDataLocation(8);
            Length = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static readonly ByteDataLocation code;
        private static readonly UInt32DataLocation serverversion;
        private static readonly ByteDataLocation isvip;
        private static readonly DataLocation blah;
        private static readonly UInt32DataLocation exechecksum;
        private static readonly DataLocation blah2;
        private static readonly ByteDataLocation demodata;
        private static readonly UInt32DataLocation codechecksum;
        private static readonly UInt32DataLocation newschecksum;
        private static readonly DataLocation blah4;
        public static readonly int Length;

        private readonly byte[] data;

        public LoginResponsePacket(byte[] data)
        {
            this.data = data;
        }

        public void Initialize()
        {
            Type = (byte)Packets.S2CPacketType.LoginResponse;
            Code = 0;
            ServerVersion = 134;
            IsVip = 0;

            for (int x = 0; x < (blah.NumBytes); x++)
                data[blah.ByteOffset + x] = 0;

            for (int x = 0; x < (blah2.NumBytes); x++)
                data[blah2.ByteOffset + x] = 0;

            ExeChecksum = 0;
            DemoData = 0;
            CodeChecksum = 0;
            NewsChecksum = 0;

            for (int x = 0; x < (blah4.NumBytes); x++)
                data[blah4.ByteOffset + x] = 0;
        }

        public byte Type
        {
            set { type.SetValue(data, value); }
        }

        public byte Code
        {
            get { return code.GetValue(data); }
            set { code.SetValue(data, value); }
        }
        
        public uint ServerVersion
        {
            set { serverversion.SetValue(data, value); }
        }

        public byte IsVip
        {
            set { isvip.SetValue(data, value); }
        }

        public uint ExeChecksum
        {
            set { exechecksum.SetValue(data, value); }
        }

        public byte DemoData
        {
            set { demodata.SetValue(data, value); }
        }

        public uint CodeChecksum
        {
            set { codechecksum.SetValue(data, value); }
        }

        public uint NewsChecksum
        {
            set { newschecksum.SetValue(data, value); }
        }
    }
}

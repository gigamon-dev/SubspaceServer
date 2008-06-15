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
            type = locationBuilder.CreateDataLocation(8);
            code = locationBuilder.CreateDataLocation(8);
            serverversion = locationBuilder.CreateDataLocation(32);
            isvip = locationBuilder.CreateDataLocation(8);
            blah = locationBuilder.CreateDataLocation(8*3);
            exechecksum = locationBuilder.CreateDataLocation(32);
            blah2 = locationBuilder.CreateDataLocation(8*5);
            demodata = locationBuilder.CreateDataLocation(8);
            codechecksum = locationBuilder.CreateDataLocation(32);
            newschecksum = locationBuilder.CreateDataLocation(32);
            blah4 = locationBuilder.CreateDataLocation(8*8);
            Length = locationBuilder.NumBytes;
        }

        private static readonly DataLocation type;
        private static readonly DataLocation code;
        private static readonly DataLocation serverversion;
        private static readonly DataLocation isvip;
        private static readonly DataLocation blah;
        private static readonly DataLocation exechecksum;
        private static readonly DataLocation blah2;
        private static readonly DataLocation demodata;
        private static readonly DataLocation codechecksum;
        private static readonly DataLocation newschecksum;
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

            for (int x = 0; x < blah.NumBits / 8; x++)
                data[blah.ByteOffset + x] = 0;

            for (int x = 0; x < blah2.NumBits / 8; x++)
                data[blah2.ByteOffset + x] = 0;

            ExeChecksum = 0;
            DemoData = 0;
            CodeChecksum = 0;
            NewsChecksum = 0;

            for (int x = 0; x < blah4.NumBits / 8; x++)
                data[blah4.ByteOffset + x] = 0;
        }

        public byte Type
        {
            //get { return ExtendedBitConverter.ToByte(data, type.ByteOffset, type.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, type.ByteOffset, type.BitOffset, type.NumBits); }
        }

        public byte Code
        {
            get { return ExtendedBitConverter.ToByte(data, code.ByteOffset, code.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, code.ByteOffset, code.BitOffset, code.NumBits); }
        }
        
        public uint ServerVersion
        {
            //get { return ExtendedBitConverter.ToUInt32(data, serverversion.ByteOffset, serverversion.BitOffset); }
            set { ExtendedBitConverter.WriteUInt32Bits(value, data, serverversion.ByteOffset, serverversion.BitOffset, serverversion.NumBits); }
        }

        public byte IsVip
        {
            //get { return ExtendedBitConverter.ToByte(data, isvip.ByteOffset, isvip.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, isvip.ByteOffset, isvip.BitOffset, isvip.NumBits); }
        }

        public uint ExeChecksum
        {
            set { ExtendedBitConverter.WriteUInt32Bits(value, data, exechecksum.ByteOffset, exechecksum.BitOffset, exechecksum.NumBits); }
        }

        public byte DemoData
        {
            set { ExtendedBitConverter.WriteByteBits(value, data, demodata.ByteOffset, demodata.BitOffset, demodata.NumBits); }
        }

        public uint CodeChecksum
        {
            set { ExtendedBitConverter.WriteUInt32Bits(value, data, codechecksum.ByteOffset, codechecksum.BitOffset, codechecksum.NumBits); }
        }

        public uint NewsChecksum
        {
            set { ExtendedBitConverter.WriteUInt32Bits(value, data, newschecksum.ByteOffset, newschecksum.BitOffset, newschecksum.NumBits); }
        }
    }
}

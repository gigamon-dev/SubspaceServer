using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct LoginResponsePacket
    {
        public byte Type;
        public byte Code;
        private uint serverVersion;
        public byte IsVip;
        private byte blah_1;
        private byte blah_2;
        private byte blah_3;
        private uint exeChecksum;
        private byte blah2_1;
        private byte blah2_2;
        private byte blah2_3;
        private byte blah2_4;
        private byte blah2_5;
        public byte DemoData;
        private uint codeChecksum;
        private uint newsChecksum;
        private byte blah4_1;
        private byte blah4_2;
        private byte blah4_3;
        private byte blah4_4;
        private byte blah4_5;
        private byte blah4_6;
        private byte blah4_7;
        private byte blah4_8;

        public uint ServerVersion
        {
            get { return LittleEndianConverter.Convert(serverVersion); }
            set { serverVersion = LittleEndianConverter.Convert(value); }
        }

        public uint ExeChecksum
        {
            get { return LittleEndianConverter.Convert(exeChecksum); }
            set { exeChecksum = LittleEndianConverter.Convert(value); }
        }

        public uint CodeChecksum
        {
            get { return LittleEndianConverter.Convert(codeChecksum); }
            set { codeChecksum = LittleEndianConverter.Convert(value); }
        }

        public uint NewsChecksum
        {
            get { return LittleEndianConverter.Convert(newsChecksum); }
            set { newsChecksum = LittleEndianConverter.Convert(value); }
        }

        public void Initialize()
        {
            Type = (byte)S2CPacketType.LoginResponse;
            Code = 0;
            ServerVersion = 134;
            IsVip = 0;
            blah_1 = blah_2 = blah_3 = 0;
            blah2_1 = blah2_2 = blah2_3 = blah2_4 = blah2_5 = 0;
            ExeChecksum = 0;
            DemoData = 0;
            CodeChecksum = 0;
            NewsChecksum = 0;
            blah4_1 = blah4_2 = blah4_3 = blah4_4 = blah4_5 = blah4_6 = blah4_7 = blah4_8 = 0;
        }
    }
}

using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TimeSyncS2CPacket
    {
        public byte T1;
        public byte T2;
        private uint clientTime;
        private uint serverTime;

        public uint ClientTime
        {
            get { return LittleEndianConverter.Convert(clientTime); }
            set { clientTime = LittleEndianConverter.Convert(value); }
        }

        public uint ServerTime
        {
            get { return LittleEndianConverter.Convert(serverTime); }
            set { serverTime = LittleEndianConverter.Convert(value); }
        }

        public void Initialize()
        {
            T1 = 0x00;
            T2 = 0x06;
        }

        public void Initialize(uint clientTime, uint serverTime)
        {
            Initialize();

            ClientTime = clientTime;
            ServerTime = serverTime;
        }
    }
}

using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2B_UserBanner
    {
        public readonly byte Type;
        private int connectionId;
        public Banner Banner;

        public S2B_UserBanner(int connectionId, in Banner banner)
        {
            Type = (byte)S2BPacketType.UserBanner;
            this.connectionId = LittleEndianConverter.Convert(connectionId);
            Banner = banner;
        }
    }
}

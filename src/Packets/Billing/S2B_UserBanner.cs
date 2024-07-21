using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2B_UserBanner(int connectionId, ref readonly Banner banner)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2B_UserBanner>();

        #endregion

        public readonly byte Type = (byte)S2BPacketType.UserBanner;
        private readonly int connectionId = LittleEndianConverter.Convert(connectionId);
        public readonly Banner Banner = banner;

        #region Helper Properties

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        #endregion
    }
}

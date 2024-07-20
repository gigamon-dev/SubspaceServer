using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct B2S_UserPacketHeader
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<B2S_UserPacketHeader>();

        #endregion

        public readonly byte Type;
        private readonly int connectionId;
        // Followed by up to 1024 bytes of data.

        #region Helper Properties

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        #endregion
    }
}

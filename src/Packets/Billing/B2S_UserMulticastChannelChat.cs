using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct B2S_UserMulticastChannelChatHeader
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<B2S_UserMulticastChannelChatHeader>();

        #endregion

        public readonly byte Type;
        public readonly byte Count;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct MulticastChannelChatRecipient
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<MulticastChannelChatRecipient>();

        #endregion

        private readonly int connectionId;
        public readonly byte Channel;

        #region Helper Properties

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        #endregion
    }
}

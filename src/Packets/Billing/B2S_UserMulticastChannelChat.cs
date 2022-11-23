using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct B2S_UserMulticastChannelChatHeader
    {
        #region Static members

        public static readonly int Length;

        static B2S_UserMulticastChannelChatHeader()
        {
            Length = Marshal.SizeOf<B2S_UserMulticastChannelChatHeader>();
        }

        #endregion

        public byte Type;
        public byte Count;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MulticastChannelChatRecipient
    {
        #region Static members

        public static readonly int Length;

        static MulticastChannelChatRecipient()
        {
            Length = Marshal.SizeOf<MulticastChannelChatRecipient>();
        }

        #endregion

        private int connectionId;
        public byte Channel;

        #region Helpers

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        #endregion
    }
}

using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct B2S_UserPacketHeader
    {
        #region Static members

        public static readonly int Length;

        static B2S_UserPacketHeader()
        {
            Length = Marshal.SizeOf<B2S_UserPacketHeader>();
        }

        #endregion

        public byte Type;
        private int connectionId;

        #region Helpers

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        #endregion
    }
}

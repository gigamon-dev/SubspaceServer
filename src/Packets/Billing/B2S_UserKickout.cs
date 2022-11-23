using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct B2S_UserKickout
    {
        #region Static members

        public static readonly int Length;

        static B2S_UserKickout()
        {
            Length = Marshal.SizeOf<B2S_UserKickout>();
        }

        #endregion

        public byte Type;
        private int connectionId;
        private ushort reason;

        #region Helpers

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        public ushort Reason => LittleEndianConverter.Convert(reason);

        #endregion
    }
}

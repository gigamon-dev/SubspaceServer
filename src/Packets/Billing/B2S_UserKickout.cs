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
        private ushort reason; // TODO: Investigate if this field should be 16 bits or 32 bits. ASSS has 16. The MGB wiki says it's 32. Probably doesn't matter since it's little endian.

        #region Helpers

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        public ushort Reason => LittleEndianConverter.Convert(reason);

        #endregion
    }
}

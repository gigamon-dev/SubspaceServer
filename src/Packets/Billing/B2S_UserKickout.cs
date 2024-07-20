using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct B2S_UserKickout
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<B2S_UserKickout>();

        #endregion

        public readonly byte Type;
        private readonly int connectionId;
        private readonly ushort reason; // TODO: Investigate if this field should be 16 bits or 32 bits. ASSS has 16. The MGB wiki says it's 32. Probably doesn't matter since it's little endian.

        #region Helper Properties

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        public ushort Reason => LittleEndianConverter.Convert(reason);

        #endregion
    }
}

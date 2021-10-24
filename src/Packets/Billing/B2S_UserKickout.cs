using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct B2S_UserKickout
    {
        public static readonly int Length;

        static B2S_UserKickout()
        {
            Length = Marshal.SizeOf<B2S_UserKickout>();
        }

        public byte Type;

        private int connectionId;
        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        private ushort reason;
        public ushort Reason => LittleEndianConverter.Convert(reason);
    }
}

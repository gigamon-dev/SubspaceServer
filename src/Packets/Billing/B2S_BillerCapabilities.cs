using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    /// <summary>
    /// What the billing server supports, sent in reply to a <see cref="S2B_ServerCapabilities"/>
    /// that has <see cref="S2B_ServerCapabilities.SupportsSessionReattach"/> set.
    /// </summary>
    /// <remarks>
    /// This is an extension. A billing server that does not know about it never sends this.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct B2S_BillerCapabilities
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<B2S_BillerCapabilities>();

        #endregion

        public readonly byte Type;
        private readonly uint bitField;
        private readonly ushort reattachWindowSeconds;

        /// <summary>
        /// The most <see cref="S2B_SessionReattachEntry"/> the billing server accepts in one packet.
        /// </summary>
        public readonly byte ReattachMaxEntries;

        /// <summary>
        /// The most <see cref="S2BPacketType.SessionReattach"/> packets the zone may have sent and not
        /// yet had answered.
        /// </summary>
        public readonly byte ReattachMaxUnanswered;

        #region Helper Properties

        private const uint SupportsSessionReattachMask = 0b00000001;

        public bool SupportsSessionReattach => (LittleEndianConverter.Convert(bitField) & SupportsSessionReattachMask) != 0;

        /// <summary>
        /// How long the zone has to ask for its sessions back, in seconds, counted from when it sent
        /// <see cref="S2BPacketType.ServerConnect"/>.
        /// </summary>
        public ushort ReattachWindowSeconds => LittleEndianConverter.Convert(reattachWindowSeconds);

        #endregion
    }
}

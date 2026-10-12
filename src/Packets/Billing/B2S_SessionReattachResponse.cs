using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    /// <summary>
    /// The billing server's answer for one player in a <see cref="S2BPacketType.SessionReattach"/>.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Accepted"/> and <see cref="AlreadyAttached"/> leave the player logged in
    /// through the billing server. A value that is not listed here means the same as the others: not
    /// logged in.
    /// </remarks>
    public enum SessionReattachStatus : byte
    {
        /// <summary>
        /// The player is logged in through the billing server again.
        /// </summary>
        Accepted = 0,

        /// <summary>
        /// The billing server is not holding a session for the player in this zone.
        /// </summary>
        NoSession = 1,

        /// <summary>
        /// The player, or the connection id, already has a live session in this zone.
        /// </summary>
        AlreadyAttached = 2,

        /// <summary>
        /// The player was banned after they logged in.
        /// </summary>
        Banned = 3,

        /// <summary>
        /// The name was used to log in somewhere since the connection was lost.
        /// </summary>
        NameUsedElsewhere = 4,

        /// <summary>
        /// The time allowed for asking has passed, or <see cref="S2BPacketType.SessionReattachEnd"/>
        /// was already sent.
        /// </summary>
        WindowClosed = 5,

        /// <summary>
        /// The name does not belong to the user id.
        /// </summary>
        NameMismatch = 6,

        /// <summary>
        /// The billing server could not answer right now. The player can be asked for again.
        /// </summary>
        TryAgain = 7,
    }

    /// <summary>
    /// Answers the <see cref="S2B_SessionReattachHeader"/> with the same <see cref="Sequence"/>.
    /// Followed by <see cref="Count"/> <see cref="B2S_SessionReattachResponseEntry"/>, one for each
    /// entry that was sent and in the same order.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct B2S_SessionReattachResponseHeader
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<B2S_SessionReattachResponseHeader>();

        #endregion

        public readonly byte Type;
        private readonly ushort sequence;
        public readonly byte Count;

        #region Helper Properties

        public ushort Sequence => LittleEndianConverter.Convert(sequence);

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct B2S_SessionReattachResponseEntry
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<B2S_SessionReattachResponseEntry>();

        #endregion

        private readonly int connectionId;
        private readonly byte status;

        #region Helper Properties

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        public SessionReattachStatus Status => (SessionReattachStatus)status;

        #endregion
    }
}

using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    public enum PlayerGroupPendingRemovedReason
    {
        /// <summary>
        /// The invitee disconnected from the server.
        /// </summary>
        Disconnect,

        /// <summary>
        /// The invitee declined.
        /// </summary>
        Decline,

        /// <summary>
        /// The inviter canceled the invite.
        /// </summary>
        InviterCancel,

        /// <summary>
        /// The inviter disconnected from the server.
        /// </summary>
        InviterDisconnect,

        /// <summary>
        /// The group is being disbanded.
        /// </summary>
        Disband,
    }

    /// <summary>
    /// Callback for when a pending invite is removed from a <see cref="IPlayerGroup"/>.
    /// </summary>
    [CallbackHelper]
    public static partial class PlayerGroupPendingRemovedCallback
    {
        public delegate void PlayerGroupPendingRemovedDelegate(IPlayerGroup group, Player player, PlayerGroupPendingRemovedReason reason);
    }
}

using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    public enum PlayerGroupMemberRemovedReason
    {
        /// <summary>
        /// The player disconnected from the server.
        /// </summary>
        Disconnect,

        /// <summary>
        /// The player decided to leave the group.
        /// </summary>
        Leave,

        /// <summary>
        /// The player was kicked out of the group.
        /// </summary>
        Kick,

        /// <summary>
        /// The group is being disbanded.
        /// </summary>
        Disband,
    }

    /// <summary>
    /// Callback delegate for when a member has been removed from a <see cref="IPlayerGroup"/>.
    /// </summary>
    /// <param name="group"></param>
    /// <param name="player"></param>
    /// <param name="reason"></param>
    [ComponentCallback]
    public delegate void PlayerGroupMemberRemovedCallback(IPlayerGroup group, Player player, PlayerGroupMemberRemovedReason reason);
}

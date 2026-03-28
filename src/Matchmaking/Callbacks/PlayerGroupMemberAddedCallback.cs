using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a member has been added to a <see cref="IPlayerGroup"/>.
    /// </summary>
    [CallbackHelper]
    public static partial class PlayerGroupMemberAddedCallback
    {
        /// <summary>
        /// Delegate for when a member has been added to a <see cref="IPlayerGroup"/>
        /// </summary>
        /// <param name="group">The group that the member was added to.</param>
        /// <param name="player">The player added to the group.</param>
        public delegate void PlayerGroupMemberAddedDelegate(IPlayerGroup group, Player player);
    }
}

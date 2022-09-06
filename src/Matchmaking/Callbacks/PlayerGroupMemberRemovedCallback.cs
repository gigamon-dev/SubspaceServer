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
    /// Callback for when a member has been removed from a <see cref="IPlayerGroup"/>.
    /// </summary>
    public static class PlayerGroupMemberRemovedCallback
    {
        public delegate void PlayerGroupMemberRemovedDelegate(IPlayerGroup group, Player player, PlayerGroupMemberRemovedReason reason);

        public static void Register(ComponentBroker broker, PlayerGroupMemberRemovedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PlayerGroupMemberRemovedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, IPlayerGroup group, Player player, PlayerGroupMemberRemovedReason reason)
        {
            broker?.GetCallback<PlayerGroupMemberRemovedDelegate>()?.Invoke(group, player, reason);

            if (broker?.Parent != null)
                Fire(broker.Parent, group, player, reason);
        }
    }
}

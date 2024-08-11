using SS.Core;
using SS.Core.ComponentInterfaces;

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
    public static class PlayerGroupPendingRemovedCallback
    {
        public delegate void PlayerGroupPendingRemovedDelegate(IPlayerGroup group, Player player, PlayerGroupPendingRemovedReason reason);

        public static void Register(IComponentBroker broker, PlayerGroupPendingRemovedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, PlayerGroupPendingRemovedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, IPlayerGroup group, Player player, PlayerGroupPendingRemovedReason reason)
        {
            broker?.GetCallback<PlayerGroupPendingRemovedDelegate>()?.Invoke(group, player, reason);

            if (broker?.Parent != null)
                Fire(broker.Parent, group, player, reason);
        }
    }
}

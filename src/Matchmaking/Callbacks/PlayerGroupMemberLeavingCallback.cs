using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a member is about to leave an <see cref="IPlayerGroup"/>.
    /// This occurs right before a member is removed from the group.
    /// </summary>
    public static class PlayerGroupMemberLeavingCallback
    {
        public delegate void PlayerGroupMemberLeavingDelegate(IPlayerGroup group, Player player);

        public static void Register(ComponentBroker broker, PlayerGroupMemberLeavingDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PlayerGroupMemberLeavingDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, IPlayerGroup group, Player player)
        {
            broker?.GetCallback<PlayerGroupMemberLeavingDelegate>()?.Invoke(group, player);

            if (broker?.Parent != null)
                Fire(broker.Parent, group, player);
        }
    }
}

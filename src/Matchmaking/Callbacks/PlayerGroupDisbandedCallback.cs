using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a <see cref="IPlayerGroup"/> disbands.
    /// </summary>
    public static class PlayerGroupDisbandedCallback
    {
        public delegate void PlayerGroupDisbandedDelegate(IPlayerGroup group);

        public static void Register(ComponentBroker broker, PlayerGroupDisbandedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PlayerGroupDisbandedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, IPlayerGroup group)
        {
            broker?.GetCallback<PlayerGroupDisbandedDelegate>()?.Invoke(group);

            if (broker?.Parent != null)
                Fire(broker.Parent, group);
        }
    }
}

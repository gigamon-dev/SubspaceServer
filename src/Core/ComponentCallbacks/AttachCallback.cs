using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="AttachDelegate"/> callback.
    /// </summary>
    public static class AttachCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> attaches or detaches.
        /// </summary>
        /// <param name="player">The player that is attaching or detaching.</param>
        /// <param name="to">The player being attached to, or <see langword="null"/> when detaching.</param>
        public delegate void AttachDelegate(Player player, Player? to);

        public static void Register(IComponentBroker broker, AttachDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, AttachDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Player player, Player? to)
        {
            broker?.GetCallback<AttachDelegate>()?.Invoke(player, to);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, to);
        }
    }
}

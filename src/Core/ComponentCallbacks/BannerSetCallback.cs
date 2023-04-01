using SS.Packets;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback for when a player's banner is set.
    /// </summary>
    public static class BannerSetCallback
    {
        /// <summary>
        /// Delegate for when a player's banner is set.
        /// </summary>
        /// <param name="player">The player whose banner was set.</param>
        /// <param name="banner">The banner.</param>
        /// <param name="isFromPlayer">Whether the change was initiated by the player themself.</param>
        public delegate void BannerSetDelegate(Player player, in Banner banner, bool isFromPlayer);

        public static void Register(ComponentBroker broker, BannerSetDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, BannerSetDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player player, in Banner banner, bool isFromPlayer)
        {
            broker?.GetCallback<BannerSetDelegate>()?.Invoke(player, banner, isFromPlayer);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, banner, isFromPlayer);
        }
    }
}

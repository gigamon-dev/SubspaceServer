using SS.Core.ComponentInterfaces;
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
        public delegate void BannerSetDelegate(Player player, ref readonly Banner banner, bool isFromPlayer);

        public static void Register(IComponentBroker broker, BannerSetDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, BannerSetDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Player player, ref readonly Banner banner, bool isFromPlayer)
        {
            broker?.GetCallback<BannerSetDelegate>()?.Invoke(player, in banner, isFromPlayer);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, in banner, isFromPlayer);
        }
    }
}

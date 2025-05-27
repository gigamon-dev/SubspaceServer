using SS.Packets;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback for when a player's banner is set.
    /// </summary>
    [CallbackHelper]
    public static partial class BannerSetCallback
    {
        /// <summary>
        /// Delegate for when a player's banner is set.
        /// </summary>
        /// <param name="player">The player whose banner was set.</param>
        /// <param name="banner">The banner.</param>
        /// <param name="isFromPlayer">Whether the change was initiated by the player themself.</param>
        public delegate void BannerSetDelegate(Player player, ref readonly Banner banner, bool isFromPlayer);
    }
}

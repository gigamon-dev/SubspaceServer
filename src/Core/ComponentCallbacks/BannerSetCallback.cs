using SS.Packets;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a player's banner is set.
    /// </summary>
    /// <param name="player">The player whose banner was set.</param>
    /// <param name="banner">The banner.</param>
    /// <param name="isFromPlayer">Whether the change was initiated by the player themself.</param>
    [ComponentCallback]
    public delegate void BannerSetCallback(Player player, ref readonly Banner banner, bool isFromPlayer);
}

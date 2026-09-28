using SS.Packets.Game;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a <see cref="Player"/> picks up a "green" (prize).
    /// </summary>
    /// <param name="player">The player that picked up a prize.</param>
    /// <param name="x">The x-coordinate.</param>
    /// <param name="y">The y-coordinate.</param>
    /// <param name="prize">The type of prize picked up.</param>
    [ComponentCallback]
    public delegate void GreenCallback(Player player, int x, int y, Prize prize);
}

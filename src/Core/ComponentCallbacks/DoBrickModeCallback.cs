using SS.Core.ComponentInterfaces;
using System.Collections.Generic;
using static SS.Core.Modules.Bricks;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for deciding brick locations when a player requests a brick drop.
    /// </summary>
    /// <param name="player">The player that made the request.</param>
    /// <param name="brickMode">The mode. An implementation should only add bricks to <paramref name="bricks"/> if the mode matches.</param>
    /// <param name="x">The x-coordinate from the request.</param>
    /// <param name="y">The y-coordinate from the request.</param>
    /// <param name="length">The maximum # of tiles a brick should span.</param>
    /// <param name="bricks">The collection to add brick locations to.</param>
    [ComponentCallback]
    public delegate void DoBrickModeCallback(Player player, BrickMode brickMode, short x, short y, int length, IList<BrickLocation> bricks);
}

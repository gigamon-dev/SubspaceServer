using SS.Packets.Game;
using System.Collections.Generic;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when brick(s) are placed.
    /// </summary>
    /// <param name="arena">The arena the brick(s) were placed in.</param>
    /// <param name="player">The player that placed the brick(s). <see langword="null"/> if placed by the server, not a player.</param>
    /// <param name="bricks">The brick(s) that were placed.</param>
    [ComponentCallback]
    public delegate void BricksPlacedCallback(Arena arena, Player? player, IReadOnlyList<BrickData> bricks);
}

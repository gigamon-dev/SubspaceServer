using SS.Packets.Game;
using System.Collections.Generic;

namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class BricksPlacedCallback
    {
        /// <summary>
        /// Delegate for when brick(s) are placed.
        /// </summary>
        /// <param name="arena">The arena the brick(s) were placed in.</param>
        /// <param name="player">The player that placed the brick(s). <see langword="null"/> if not placed by a player.</param>
        /// <param name="bricks">The brick(s) that were placed.</param>
        public delegate void BricksPlacedDelegate(Arena arena, Player? player, IReadOnlyList<BrickData> bricks);
    }
}

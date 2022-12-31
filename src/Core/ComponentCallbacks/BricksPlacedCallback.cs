using SS.Packets.Game;
using System.Collections.Generic;

namespace SS.Core.ComponentCallbacks
{
    public static class BricksPlacedCallback
    {
        /// <summary>
        /// Delegate for when brick(s) are placed.
        /// </summary>
        /// <param name="arena">The arena the brick(s) were placed in.</param>
        /// <param name="player">The player that placed the brick(s). <see langword="null"/> if not placed by a player.</param>
        /// <param name="bricks">The brick(s) that were placed.</param>
        public delegate void BricksPlacedDelegate(Arena arena, Player player, IReadOnlyList<BrickData> bricks);

        public static void Register(ComponentBroker broker, BricksPlacedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, BricksPlacedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, Player player, IReadOnlyList<BrickData> bricks)
        {
            broker?.GetCallback<BricksPlacedDelegate>()?.Invoke(arena, player, bricks);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, player, bricks);
        }
    }
}

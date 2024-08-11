using SS.Core.ComponentInterfaces;
using System.Collections.Generic;
using static SS.Core.Modules.Bricks;

namespace SS.Core.ComponentCallbacks
{
    public static class DoBrickModeCallback
    {
        /// <summary>
        /// Delegate for deciding brick locations when a player requests a brick drop.
        /// </summary>
        /// <param name="player">The player that made the request.</param>
        /// <param name="brickMode">The mode. An implementation should only add bricks to <paramref name="bricks"/> if the mode matches.</param>
        /// <param name="x">The x-coordinate from the request.</param>
        /// <param name="y">The y-coordinate from the request.</param>
        /// <param name="length">The maximum # of tiles a brick should span.</param>
        /// <param name="bricks">The collection to add brick locations to.</param>
        public delegate void DoBrickModeDelegate(Player player, BrickMode brickMode, short x, short y, int length, IList<BrickLocation> bricks);

        public static void Register(IComponentBroker broker, DoBrickModeDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, DoBrickModeDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Player player, BrickMode brickMode, short x, short y, int length, IList<BrickLocation> bricks)
        {
            broker?.GetCallback<DoBrickModeDelegate>()?.Invoke(player, brickMode, x, y, length, bricks);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, brickMode, x, y, length, bricks);
        }
    }
}

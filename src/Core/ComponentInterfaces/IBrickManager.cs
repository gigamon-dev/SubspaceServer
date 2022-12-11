using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for managing bricks.
    /// </summary>
    public interface IBrickManager : IComponentInterface
    {
        /// <summary>
        /// Places a brick.
        /// </summary>
        /// <param name="arena">The arena to place a brick in.</param>
        /// <param name="freq">The team the brick belongs to.</param>
        /// <param name="x1">The starting x-coordinate.</param>
        /// <param name="y1">The starting y-coordinate.</param>
        /// <param name="x2">The ending x-coordinate.</param>
        /// <param name="y2">The ending y-coordinate.</param>
        void DropBrick(Arena arena, short freq, short x1, short y1, short x2, short y2);
    }

    /// <summary>
    /// Describes the location of a brick.
    /// </summary>
    public readonly struct BrickLocation
    {
        /// <summary>
        /// Starting x-coordinate.
        /// </summary>
        public readonly short X1;

        /// <summary>
        /// Starting y-coordinate.
        /// </summary>
        public readonly short Y1;

        /// <summary>
        /// Ending x-coordinate.
        /// </summary>
        public readonly short X2;

        /// <summary>
        /// Ending y-coordinate.
        /// </summary>
        public readonly short Y2;

        public BrickLocation(short x1, short y1, short x2, short y2)
        {
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
        }
    }

    /// <summary>
    /// Interface for handling brick placement.
    /// </summary>
    /// <remarks>
    /// It is recommended to add additional modes using <see cref="ComponentCallbacks.DoBrickModeCallback"/> rather than override <see cref="IBrickHandler"/>.
    /// </remarks>
    public interface IBrickHandler : IComponentInterface
    {
        /// <summary>
        /// Handles a brick drop request from a player.
        /// </summary>
        /// <param name="player">The player that sent the request.</param>
        /// <param name="x">The x-coordinate of the request, from the <see cref="Packets.Game.C2S_Brick"/> packet.</param>
        /// <param name="y">The y-coordinate of the request, from the <see cref="Packets.Game.C2S_Brick"/> packet.</param>
        /// <param name="bricks">The list to add locations to. Note, a handler can decide to drop more than one brick per request.</param>
        void HandleBrick(Player player, short x, short y, IList<BrickLocation> bricks);
    }
}

using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Describes the position of a brick.
    /// </summary>
    public readonly struct Brick
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

        public Brick(short x1, short y1, short x2, short y2)
        {
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
        }
    }

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
    /// Interface for handling brick placement.
    /// </summary>
    /// <remarks>
    /// It is recommended to add additional modes using <see cref="ComponentCallbacks.DoBrickModeCallback"/> rather than override <see cref="IBrickHandler"/>.
    /// </remarks>
    public interface IBrickHandler : IComponentInterface
    {
        void HandleBrick(Player p, short x, short y, in ICollection<Brick> bricks);
    }
}

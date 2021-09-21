using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    public readonly struct Brick
    {
        public readonly short X1;
        public readonly short Y1;

        public readonly short X2;
        public readonly short Y2;

        public Brick(short x1, short y1, short x2, short y2)
        {
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
        }
    }

    public interface IBrickManager : IComponentInterface
    {
        void DropBrick(Arena arena, short freq, short x1, short y1, short x2, short y2);
    }

    public interface IBrickHandler : IComponentInterface
    {
        void HandleBrick(Player p, short x, short y, in ICollection<Brick> bricks);
    }
}

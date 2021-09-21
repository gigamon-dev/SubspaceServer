using SS.Core.ComponentInterfaces;
using System.Collections.Generic;
using static SS.Core.Modules.Bricks;

namespace SS.Core.ComponentCallbacks
{
    public static class DoBrickModeCallback
    {
        public delegate void DoBrickModeDelegate(Player player, BrickMode brickMode, short x, short y, int length, in ICollection<Brick> bricks);

        public static void Register(ComponentBroker broker, DoBrickModeDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, DoBrickModeDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player player, BrickMode brickMode, short x, short y, int length, in ICollection<Brick> bricks)
        {
            broker?.GetCallback<DoBrickModeDelegate>()?.Invoke(player, brickMode, x, y, length, in bricks);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, brickMode, x, y, length, in bricks);
        }
    }
}

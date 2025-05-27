using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class BallGoalCallback
    {
        public delegate void BallGoalDelegate(Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates);
    }
}

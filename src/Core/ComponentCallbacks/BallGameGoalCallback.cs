using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="BallGameGoalCallback"/>.
    /// </summary>
    /// <remarks>
    /// The difference between this and <see cref="BallGoalCallback"/> is that 
    /// <see cref="BallGoalCallback"/> is fired when a ball goes into a goal.
    /// Whereas this callback occurs later, after ball game scores have been updated.
    /// </remarks>
    [CallbackHelper]
    public static partial class BallGameGoalCallback
    {
        public delegate void BallGameGoalDelegate(Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates);
    }
}

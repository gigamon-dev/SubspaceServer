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
    public static class BallGameGoalCallback
    {
        public delegate void BallGameGoalDelegate(Arena arena, Player player, byte ballId, MapCoordinate coordinate);

        public static void Register(ComponentBroker broker, BallGameGoalDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, BallGameGoalDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, Player player, byte ballId, MapCoordinate coordinate)
        {
            broker?.GetCallback<BallGameGoalDelegate>()?.Invoke(arena, player, ballId, coordinate);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, player, ballId, coordinate);
        }
    }
}

using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    public static class BallGoalCallback
    {
        public delegate void BallGoalDelegate(Arena arena, Player player, byte ballId, MapCoordinate coordinate);

        public static void Register(ComponentBroker broker, BallGoalDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, BallGoalDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, Player player, byte ballId, MapCoordinate coordinate)
        {
            broker?.GetCallback<BallGoalDelegate>()?.Invoke(arena, player, ballId, coordinate);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, player, ballId, coordinate);
        }
    }
}

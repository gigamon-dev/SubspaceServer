using SS.Core.ComponentInterfaces;
using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    public static class BallGoalCallback
    {
        public delegate void BallGoalDelegate(Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates);

        public static void Register(IComponentBroker broker, BallGoalDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, BallGoalDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates)
        {
            broker?.GetCallback<BallGoalDelegate>()?.Invoke(arena, player, ballId, goalCoordinates);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, player, ballId, goalCoordinates);
        }
    }
}

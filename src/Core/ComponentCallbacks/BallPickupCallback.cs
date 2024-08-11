using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    public static class BallPickupCallback
    {
        public delegate void BallPickupDelegate(Arena arena, Player player, byte ballId);

        public static void Register(IComponentBroker broker, BallPickupDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, BallPickupDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena, Player player, byte ballId)
        {
            broker?.GetCallback<BallPickupDelegate>()?.Invoke(arena, player, ballId);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, player, ballId);
        }
    }
}

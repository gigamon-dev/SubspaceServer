namespace SS.Core.ComponentCallbacks
{
    public static class BallPickupCallback
    {
        public delegate void BallPickupDelegate(Arena arena, Player p, byte ballId);

        public static void Register(ComponentBroker broker, BallPickupDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, BallPickupDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, Player p, byte ballId)
        {
            broker?.GetCallback<BallPickupDelegate>()?.Invoke(arena, p, ballId);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, p, ballId);
        }
    }
}

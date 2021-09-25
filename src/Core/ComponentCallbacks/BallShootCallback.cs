namespace SS.Core.ComponentCallbacks
{
    public static class BallShootCallback
    {
        public delegate void BallShootDelegate(Arena arena, Player p, byte ballId);

        public static void Register(ComponentBroker broker, BallShootDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, BallShootDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, Player p, byte ballId)
        {
            broker?.GetCallback<BallShootDelegate>()?.Invoke(arena, p, ballId);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, p, ballId);
        }
    }
}

namespace SS.Core.ComponentCallbacks
{
    public static class WarpCallback
    {
        public delegate void WarpDelegate(Player player, int oldX, int oldY, int newX, int newY);

        public static void Register(ComponentBroker broker, WarpDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, WarpDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player player, int oldX, int oldY, int newX, int newY)
        {
            broker?.GetCallback<WarpDelegate>()?.Invoke(player, oldX, oldY, newX, newY);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, oldX, oldY, newX, newY);
        }
    }
}

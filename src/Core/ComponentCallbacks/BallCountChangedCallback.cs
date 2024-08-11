using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    public static class BallCountChangedCallback
    {
        public delegate void BallCountChangedDelegate(Arena arena, int newCount, int oldCount);

        public static void Register(IComponentBroker broker, BallCountChangedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, BallCountChangedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena, int newCount, int oldCount)
        {
            broker?.GetCallback<BallCountChangedDelegate>()?.Invoke(arena, newCount, oldCount);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, newCount, oldCount);
        }
    }
}

using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    public static class PersistIntervalEndedCallback
    {
        public delegate void PersistIntervalEndedDelegate(PersistInterval interval);

        public static void Register(ComponentBroker broker, PersistIntervalEndedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PersistIntervalEndedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, PersistInterval interval)
        {
            broker?.GetCallback<PersistIntervalEndedDelegate>()?.Invoke(interval);

            if (broker?.Parent != null)
                Fire(broker.Parent, interval);
        }
    }
}

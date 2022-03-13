using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="PersistIntervalEndedDelegate"/> callback
    /// which is fired by the <see cref="Modules.Persist"/> module after it ends an interval.
    /// </summary>
    public static class PersistIntervalEndedCallback
    {
        public delegate void PersistIntervalEndedDelegate(PersistInterval interval, string arenaGroup);

        public static void Register(ComponentBroker broker, PersistIntervalEndedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PersistIntervalEndedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, PersistInterval interval, string arenaGroup)
        {
            broker?.GetCallback<PersistIntervalEndedDelegate>()?.Invoke(interval, arenaGroup);

            if (broker?.Parent != null)
                Fire(broker.Parent, interval, arenaGroup);
        }
    }
}

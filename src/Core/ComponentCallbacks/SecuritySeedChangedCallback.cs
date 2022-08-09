namespace SS.Core.ComponentCallbacks
{
    public static class SecuritySeedChangedCallback
    {
        public delegate void SecuritySeedChangedDelegate(uint greenSeed, uint doorSeed, uint timestamp);

        public static void Register(ComponentBroker broker, SecuritySeedChangedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, SecuritySeedChangedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, uint greenSeed, uint doorSeed, uint timestamp)
        {
            broker?.GetCallback<SecuritySeedChangedDelegate>()?.Invoke(greenSeed, doorSeed, timestamp);

            if (broker?.Parent != null)
                Fire(broker.Parent, greenSeed, doorSeed, timestamp);
        }
    }
}

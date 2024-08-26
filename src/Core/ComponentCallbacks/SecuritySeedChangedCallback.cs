using SS.Core.ComponentInterfaces;
using SS.Utilities;

namespace SS.Core.ComponentCallbacks
{
    public static class SecuritySeedChangedCallback
    {
        public delegate void SecuritySeedChangedDelegate(uint greenSeed, uint doorSeed, ServerTick timestamp);

        public static void Register(IComponentBroker broker, SecuritySeedChangedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, SecuritySeedChangedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, uint greenSeed, uint doorSeed, ServerTick timestamp)
        {
            broker?.GetCallback<SecuritySeedChangedDelegate>()?.Invoke(greenSeed, doorSeed, timestamp);

            if (broker?.Parent != null)
                Fire(broker.Parent, greenSeed, doorSeed, timestamp);
        }
    }
}

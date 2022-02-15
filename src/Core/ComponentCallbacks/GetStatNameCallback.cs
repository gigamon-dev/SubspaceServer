namespace SS.Core.ComponentCallbacks
{
    public static class GetStatNameCallback
    {
        public delegate void GetStatNameDelegate(int statId, ref string statName);

        public static void Register(ComponentBroker broker, GetStatNameDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, GetStatNameDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, int statId, ref string statName)
        {
            statName = null;

            broker?.GetCallback<GetStatNameDelegate>()?.Invoke(statId, ref statName);

            if (broker?.Parent != null)
                Fire(broker.Parent, statId, ref statName);
        }
    }
}

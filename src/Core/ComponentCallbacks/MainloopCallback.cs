namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="MainloopDelegate"/> callback.
    /// </summary>
    public static class MainloopCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked once per iteration of the main loop.
        /// </summary>
        public delegate void MainloopDelegate();

        public static void Register(ComponentBroker broker, MainloopDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, MainloopDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker)
        {
            broker?.GetCallback<MainloopDelegate>()?.Invoke();

            if (broker?.Parent != null)
                Fire(broker.Parent);
        }
    }
}

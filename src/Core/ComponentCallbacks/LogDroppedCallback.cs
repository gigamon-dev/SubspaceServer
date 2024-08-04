namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="LogDroppedDelegate"/> callback.
    /// </summary>
    public static class LogDroppedCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a log entry is dropped (not written),
        /// due to the logging infrastructure having too much back pressure.
        /// </summary>
        /// <remarks>
        /// This is NOT executed on the mainloop thread.
        /// </remarks>
        /// <param name="totalDropped">The total # of log entries dropped.</param>
        public delegate void LogDroppedDelegate(int totalDropped);

        public static void Register(ComponentBroker broker, LogDroppedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, LogDroppedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, int totalDropped)
        {
            broker?.GetCallback<LogDroppedDelegate>()?.Invoke(totalDropped);

            if (broker?.Parent != null)
                Fire(broker.Parent, totalDropped);
        }
    }
}

using SS.Core.ComponentInterfaces;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="LogDelegate"/> callback.
    /// </summary>
    public static class LogCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a log is to be written.
        /// </summary>
        /// <param name="message">The log message to write.</param>
        public delegate void LogDelegate(in LogEntry logEntry);

        public static void Register(ComponentBroker broker, LogDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, LogDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, in LogEntry logEntry)
        {
            broker?.GetCallback<LogDelegate>()?.Invoke(in logEntry);

            if (broker?.Parent != null)
                Fire(broker.Parent, in logEntry);
        }
    }
}

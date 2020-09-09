using System;
using System.Collections.Generic;
using System.Linq;
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
        public delegate void LogDelegate(string message);

        public static void Register(ComponentBroker broker, LogDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, LogDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, string message)
        {
            broker?.GetCallback<LogDelegate>()?.Invoke(message);

            if (broker?.Parent != null)
                Fire(broker.Parent, message);
        }
    }
}

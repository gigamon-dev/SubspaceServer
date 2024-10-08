﻿using SS.Core.ComponentInterfaces;

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
        public delegate void LogDelegate(ref readonly LogEntry logEntry);

        public static void Register(IComponentBroker broker, LogDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, LogDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, ref readonly LogEntry logEntry)
        {
            broker?.GetCallback<LogDelegate>()?.Invoke(in logEntry);

            if (broker?.Parent != null)
                Fire(broker.Parent, in logEntry);
        }
    }
}

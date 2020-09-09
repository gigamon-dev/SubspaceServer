using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    public static class LogCallback
    {
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

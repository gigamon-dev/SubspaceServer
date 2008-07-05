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
            broker.RegisterCallback<string>(Constants.Events.Log, new ComponentCallbackDelegate<string>(handler));
        }

        public static void Unregister(ComponentBroker broker, LogDelegate handler)
        {
            broker.UnRegisterCallback<string>(Constants.Events.Log, new ComponentCallbackDelegate<string>(handler));
        }

        public static void Fire(ComponentBroker broker, string message)
        {
            broker.DoCallback<string>(Constants.Events.Log, message);
        }
    }
}

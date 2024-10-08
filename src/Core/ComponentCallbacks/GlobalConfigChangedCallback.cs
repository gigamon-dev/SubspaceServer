﻿using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="GlobalConfigChangedDelegate"/> callback.
    /// </summary>
    public static class GlobalConfigChangedCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when the global.conf file was changed.
        /// </summary>
        public delegate void GlobalConfigChangedDelegate();

        public static void Register(IComponentBroker broker, GlobalConfigChangedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, GlobalConfigChangedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker)
        {
            broker?.GetCallback<GlobalConfigChangedDelegate>()?.Invoke();

            if (broker?.Parent != null)
                Fire(broker.Parent);
        }
    }
}

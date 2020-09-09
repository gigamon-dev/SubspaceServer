using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    public delegate void ArenaActionDelegate(Arena arena, ArenaAction action);

    /// <summary>
    /// helper class for working with the ArenaAction event
    /// </summary>
    public static class ArenaActionCallback
    {
        public static void Register(ComponentBroker broker, ArenaActionDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, ArenaActionDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, ArenaAction action)
        {
            broker?.GetCallback<ArenaActionDelegate>()?.Invoke(arena, action);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, action);
        }
    }
}

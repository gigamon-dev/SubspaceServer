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
            broker.RegisterCallback<Arena, ArenaAction>(Constants.Events.ArenaAction, new ComponentCallbackDelegate<Arena, ArenaAction>(handler));
        }

        public static void Unregister(ComponentBroker broker, ArenaActionDelegate handler)
        {
            broker.UnRegisterCallback<Arena, ArenaAction>(Constants.Events.ArenaAction, new ComponentCallbackDelegate<Arena, ArenaAction>(handler));
        }

        public static void Fire(ComponentBroker broker, Arena arena, ArenaAction action)
        {
            broker.DoCallback<Arena, ArenaAction>(Constants.Events.ArenaAction, arena, action);
        }
    }
}

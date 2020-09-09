using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// This callback is called whenever a Player is allocated or
    /// deallocated. in general you probably want to use CB_PLAYERACTION
    /// instead of this callback for general initialization tasks.
    /// </summary>
    public static class NewPlayerCallback
    {
        public delegate void NewPlayerDelegate(Player p, bool isNew);

        public static void Register(ComponentBroker broker, NewPlayerDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, NewPlayerDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player p, bool isNew)
        {
            broker?.GetCallback<NewPlayerDelegate>()?.Invoke(p, isNew);

            if (broker?.Parent != null)
                Fire(broker.Parent, p, isNew);
        }
    }
}

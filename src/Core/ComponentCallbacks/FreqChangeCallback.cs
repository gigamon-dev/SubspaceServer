using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    public static class FreqChangeCallback
    {
        public delegate void FreqChangeDelegate(Player p, short freq);

        public static void Register(ComponentBroker broker, FreqChangeDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, FreqChangeDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player p, short freq)
        {
            broker?.GetCallback<FreqChangeDelegate>()?.Invoke(p, freq);

            if (broker?.Parent != null)
                Fire(broker.Parent, p, freq);
        }
    }
}

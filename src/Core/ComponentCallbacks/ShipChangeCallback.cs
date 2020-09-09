using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    public static class ShipChangeCallback
    {
        public delegate void ShipChangeDelegate(Player p, ShipType ship, short freq);

        public static void Register(ComponentBroker broker, ShipChangeDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, ShipChangeDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player p, ShipType ship, short freq)
        {
            broker?.GetCallback<ShipChangeDelegate>()?.Invoke(p, ship, freq);

            if (broker?.Parent != null)
                Fire(broker.Parent, p, ship, freq);
        }
    }
}

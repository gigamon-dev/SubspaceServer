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
            broker.RegisterCallback<Player, ShipType, short>(Constants.Events.ShipChange, new ComponentCallbackDelegate<Player, ShipType, short>(handler));
        }

        public static void Unregister(ComponentBroker broker, ShipChangeDelegate handler)
        {
            broker.UnRegisterCallback<Player, ShipType, short>(Constants.Events.ShipChange, new ComponentCallbackDelegate<Player, ShipType, short>(handler));
        }

        public static void Fire(ComponentBroker broker, Player p, ShipType ship, short freq)
        {
            broker.DoCallback<Player, ShipType, short>(Constants.Events.ShipChange, p, ship, freq);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;

namespace SS.Core.ComponentCallbacks
{
    public static class GreenCallback
    {
        public delegate void GreenDelegate(Player p, int x, int y, Prize prize);

        public static void Register(ComponentBroker broker, GreenDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, GreenDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player p, int x, int y, Prize prize)
        {
            broker?.GetCallback<GreenDelegate>()?.Invoke(p, x, y, prize);

            if (broker?.Parent != null)
                Fire(broker.Parent, p, x, y, prize);
        }
    }
}

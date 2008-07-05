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
            broker.RegisterCallback<Player, int, int, Prize>(Constants.Events.Green, new ComponentCallbackDelegate<Player, int, int, Prize>(handler));
        }

        public static void Unregister(ComponentBroker broker, GreenDelegate handler)
        {
            broker.UnRegisterCallback<Player, int, int, Prize>(Constants.Events.Green, new ComponentCallbackDelegate<Player, int, int, Prize>(handler));
        }

        public static void Fire(ComponentBroker broker, Player p, int x, int y, Prize prize)
        {
            broker.DoCallback<Player, int, int, Prize>(Constants.Events.Green, p, x, y, prize);
        }
    }
}

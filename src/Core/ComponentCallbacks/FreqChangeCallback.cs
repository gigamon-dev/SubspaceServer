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
            broker.RegisterCallback<Player, short>(Constants.Events.FreqChange, new ComponentCallbackDelegate<Player, short>(handler));
        }

        public static void Unregister(ComponentBroker broker, FreqChangeDelegate handler)
        {
            broker.UnRegisterCallback<Player, short>(Constants.Events.FreqChange, new ComponentCallbackDelegate<Player, short>(handler));
        }

        public static void Fire(ComponentBroker broker, Player p, short freq)
        {
            broker.DoCallback<Player, short>(Constants.Events.FreqChange, p, freq);
        }
    }
}

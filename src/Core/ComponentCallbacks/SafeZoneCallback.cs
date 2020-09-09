using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;

namespace SS.Core.ComponentCallbacks
{
    public delegate void SafeZoneDelegate(Player p, int x, int y, PlayerPositionStatus status);

    public static class SafeZoneCallback
    {
        public static void Register(ComponentBroker broker, SafeZoneDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, SafeZoneDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player p, int x, int y, PlayerPositionStatus status)
        {
            broker?.GetCallback<SafeZoneDelegate>()?.Invoke(p, x, y, status);

            if (broker?.Parent != null)
                Fire(broker.Parent, p, x, y, status);
        }
    }
}

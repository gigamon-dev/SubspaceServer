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
            broker.RegisterCallback<Player, int, int, PlayerPositionStatus>(Constants.Events.SafeZone, new ComponentCallbackDelegate<Player, int, int, PlayerPositionStatus>(handler));
        }

        public static void Unregister(ComponentBroker broker, SafeZoneDelegate handler)
        {
            broker.UnRegisterCallback<Player, int, int, PlayerPositionStatus>(Constants.Events.SafeZone, new ComponentCallbackDelegate<Player, int, int, PlayerPositionStatus>(handler));
        }

        public static void Fire(ComponentBroker broker, Player p, int x, int y, PlayerPositionStatus status)
        {
            broker.DoCallback<Player, int, int, PlayerPositionStatus>(Constants.Events.SafeZone, p, x, y, status);
        }
    }
}

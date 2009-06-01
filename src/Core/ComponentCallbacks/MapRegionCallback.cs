using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    public static class MapRegionCallback
    {
        public delegate void MapRegionDelegate(Player p, MapRegion region, short x, short y, bool entering);

        public static void Register(ComponentBroker broker, MapRegionDelegate handler)
        {
            broker.RegisterCallback<Player, MapRegion, short, short, bool>(Constants.Events.MapRegion, new ComponentCallbackDelegate<Player,MapRegion,short,short,bool>(handler));
        }

        public static void Unregister(ComponentBroker broker, MapRegionDelegate handler)
        {
            broker.UnRegisterCallback<Player, MapRegion, short, short, bool>(Constants.Events.MapRegion, new ComponentCallbackDelegate<Player, MapRegion, short, short, bool>(handler));
        }

        public static void Fire(ComponentBroker broker, Player p, MapRegion region, short x, short y, bool entering)
        {
            broker.DoCallback<Player, MapRegion, short, short, bool>(Constants.Events.MapRegion, p, region, x, y, entering);
        }
    }
}

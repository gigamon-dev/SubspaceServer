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
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, MapRegionDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player p, MapRegion region, short x, short y, bool entering)
        {
            broker?.GetCallback<MapRegionDelegate>()?.Invoke(p, region, x, y, entering);

            if (broker?.Parent != null)
                Fire(broker.Parent, p, region, x, y, entering);
        }
    }
}

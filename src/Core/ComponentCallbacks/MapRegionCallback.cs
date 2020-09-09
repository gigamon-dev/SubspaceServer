using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="MapRegionDelegate"/> callback.
    /// </summary>
    public static class MapRegionCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> enters or exits a <see cref="MapRegion"/>.
        /// </summary>
        /// <param name="p">The player entering or exiting a region.</param>
        /// <param name="region">The region being entered or exited.</param>
        /// <param name="x">The x-coordinate of the player.</param>
        /// <param name="y">The y-coordinate of the player.</param>
        /// <param name="entering">True if the region is being entered.  False if being exited.</param>
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

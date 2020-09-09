using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="ShipChangeDelegate"/> callback.
    /// </summary>
    public static class ShipChangeCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a player's ship is changed.
        /// </summary>
        /// <param name="p">The player whose ship was changed.</param>
        /// <param name="ship">The type of ship that the player was changed to.</param>
        /// <param name="freq">The team the player is on.</param>
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

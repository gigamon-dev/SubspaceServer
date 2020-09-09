using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="SafeZoneDelegate"/> callback.
    /// </summary>
    public static class SafeZoneCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> enters or exits a safe zone.
        /// </summary>
        /// <param name="p">The player that entered or exited a safe zone.</param>
        /// <param name="x">The x-coordinate of the player.</param>
        /// <param name="y">The y-coordinate of the player.</param>
        /// <param name="status">Flags about the player's position. Note: <see cref="PlayerPositionStatus.Safezone"/> indicates whether the player is in a safe zone (entered or exited).</param>
        public delegate void SafeZoneDelegate(Player p, int x, int y, PlayerPositionStatus status);

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

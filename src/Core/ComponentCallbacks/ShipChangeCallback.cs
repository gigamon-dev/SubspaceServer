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

    /// <summary>
    /// Helper class for the <see cref="PreShipFreqChangeCallback"/> callback.
    /// </summary>
    public static class PreShipFreqChangeCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a player's ship or freq (team) is changed.
        /// Intended for internal or core use only. 
        /// No recursive shipchanges should happen as a result of this callback.
        /// </summary>
        /// <param name="p">The player whose ship was changed.</param>
        /// <param name="newShip">The type of ship that the player changed to.</param>
        /// <param name="oldShip">The type of ship that the player changed from.</param>
        /// <param name="newFreq">The the player's new team.</param>
        /// <param name="oldFreq">The the player's old team.</param>
        public delegate void PreShipFreqChangeDelegate(Player p, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq);

        public static void Register(ComponentBroker broker, PreShipFreqChangeDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PreShipFreqChangeDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player p, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            broker?.GetCallback<PreShipFreqChangeDelegate>()?.Invoke(p, newShip, oldShip, newFreq, oldFreq);

            if (broker?.Parent != null)
                Fire(broker.Parent, p, newShip, oldShip, newFreq, oldFreq);
        }
    }

    /// <summary>
    /// Helper class for the <see cref="ShipFreqChangeDelegate"/> callback.
    /// </summary>
    public static class ShipFreqChangeCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a player's ship or freq (team) is changed.
        /// </summary>
        /// <param name="p">The player whose ship was changed.</param>
        /// <param name="newShip">The type of ship that the player changed to.</param>
        /// <param name="oldShip">The type of ship that the player changed from.</param>
        /// <param name="newFreq">The the player's new team.</param>
        /// <param name="oldFreq">The the player's old team.</param>
        public delegate void ShipFreqChangeDelegate(Player p, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq);

        public static void Register(ComponentBroker broker, ShipFreqChangeDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, ShipFreqChangeDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player p, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            broker?.GetCallback<ShipFreqChangeDelegate>()?.Invoke(p, newShip, oldShip, newFreq, oldFreq);

            if (broker?.Parent != null)
                Fire(broker.Parent, p, newShip, oldShip, newFreq, oldFreq);
        }
    }
}

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="PreShipFreqChangeCallback"/> callback.
    /// </summary>
    public static class PreShipFreqChangeCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a player's ship or freq (team) is changed.
        /// Intended for internal or core use only. 
        /// No recursive ship changes should happen as a result of this callback.
        /// </summary>
        /// <param name="player">The player whose ship was changed.</param>
        /// <param name="newShip">The type of ship that the player changed to.</param>
        /// <param name="oldShip">The type of ship that the player changed from.</param>
        /// <param name="newFreq">The the player's new team.</param>
        /// <param name="oldFreq">The the player's old team.</param>
        public delegate void PreShipFreqChangeDelegate(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq);

        public static void Register(ComponentBroker broker, PreShipFreqChangeDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PreShipFreqChangeDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            broker?.GetCallback<PreShipFreqChangeDelegate>()?.Invoke(player, newShip, oldShip, newFreq, oldFreq);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, newShip, oldShip, newFreq, oldFreq);
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
        /// <param name="player">The player whose ship was changed.</param>
        /// <param name="newShip">The type of ship that the player changed to.</param>
        /// <param name="oldShip">The type of ship that the player changed from.</param>
        /// <param name="newFreq">The the player's new team.</param>
        /// <param name="oldFreq">The the player's old team.</param>
        public delegate void ShipFreqChangeDelegate(Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq);

        public static void Register(ComponentBroker broker, ShipFreqChangeDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, ShipFreqChangeDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player player, ShipType newShip, ShipType oldShip, short newFreq, short oldFreq)
        {
            broker?.GetCallback<ShipFreqChangeDelegate>()?.Invoke(player, newShip, oldShip, newFreq, oldFreq);

            if (broker?.Parent != null)
                Fire(broker.Parent, player, newShip, oldShip, newFreq, oldFreq);
        }
    }
}

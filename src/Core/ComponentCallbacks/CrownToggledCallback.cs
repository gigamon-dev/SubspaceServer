namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a player's crown is toggled.
    /// </summary>
    /// <param name="player">The player whose crown was toggled.</param>
    /// <param name="on">True if the crown was turned on. False if the crown was turned off.</param>
    [ComponentCallback]
    public delegate void CrownToggledCallback(Player player, bool on);
}

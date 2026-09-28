namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a <see cref="Player"/> enters or exits a safe zone.
    /// </summary>
    /// <param name="player">The player that entered or exited a safe zone.</param>
    /// <param name="x">The x-coordinate of the player.</param>
    /// <param name="y">The y-coordinate of the player.</param>
    /// <param name="entering">True if the player is entering a safe zone.  False if the player is exiting a safe zone.</param>
    [ComponentCallback]
    public delegate void SafeZoneCallback(Player player, int x, int y, bool entering);
}

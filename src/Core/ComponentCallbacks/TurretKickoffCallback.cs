namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a player kicks off attached players.
    /// </summary>
    /// <param name="player">The player that kicked off attached players.</param>
    [ComponentCallback]
    public delegate void TurretKickoffCallback(Player player);
}

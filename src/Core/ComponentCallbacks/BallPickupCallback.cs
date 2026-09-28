namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a ball is picked up by a player.
    /// </summary>
    /// <param name="arena">The arena the ball event occured in.</param>
    /// <param name="player">The player that picked up the ball.</param>
    /// <param name="ballId">ID of the ball that was picked up.</param>
    [ComponentCallback]
    public delegate void BallPickupCallback(Arena arena, Player player, byte ballId);
}

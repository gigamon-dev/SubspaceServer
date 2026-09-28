namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a player warps.
    /// </summary>
    /// <param name="player">The player that warped.</param>
    /// <param name="oldX">The old x-coordinate.</param>
    /// <param name="oldY">The old y-coordinate.</param>
    /// <param name="newX">The new x-coordinate.</param>
    /// <param name="newY">The new y-coordinate.</param>
    [ComponentCallback]
    public delegate void WarpCallback(Player player, int oldX, int oldY, int newX, int newY);
}

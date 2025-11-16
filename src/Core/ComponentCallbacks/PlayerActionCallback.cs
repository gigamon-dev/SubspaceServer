namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate when a <see cref="Player"/>'s life-cycle state changes.
    /// </summary>
    /// <param name="player">The player that changed state.</param>
    /// <param name="action">The new state.</param>
    /// <param name="arena">The <see cref="Arena"/> the player is in. <see langword="null"/> if the player is not in an <see cref="Arena"/>.</param>
    [ComponentCallback]
    public delegate void PlayerActionCallback(Player player, PlayerAction action, Arena? arena);
}

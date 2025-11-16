namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a player changes who they're spectating.
    /// </summary>
    /// <param name="player">The player who's spectating state changed.</param>
    /// <param name="target">The player being spectated. <see langword="null"/> for removal.</param>
    [ComponentCallback]
    public delegate void SpectateChangedCallback(Player player, Player? target);
}

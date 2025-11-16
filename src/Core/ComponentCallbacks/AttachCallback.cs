namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a <see cref="Player"/> attaches or detaches.
    /// </summary>
    /// <param name="player">The player that is attaching or detaching.</param>
    /// <param name="to">The player being attached to, or <see langword="null"/> when detaching.</param>
    [ComponentCallback]
    public delegate void AttachCallback(Player player, Player? to);
}

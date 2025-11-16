namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a <see cref="Player"/> is allocated or deallocated.
    /// </summary>
    /// <remarks>
    /// In general you probably want to use the <see cref="PlayerActionCallback"/>
    /// instead of this for general initialization tasks.
    /// </remarks>
    /// <param name="player">The player being allocated or deallocated.</param>
    /// <param name="isNew">True if being allocated, false if being deallocated.</param>
    [ComponentCallback]
    public delegate void NewPlayerCallback(Player player, bool isNew);
}

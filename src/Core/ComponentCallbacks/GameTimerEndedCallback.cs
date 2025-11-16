namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate when a game timer ends.
    /// </summary>
    /// <remarks>
    /// Also consider using the <see cref="GameTimerChangedCallback"/>.
    /// </remarks>
    /// <param name="arena">The arena.</param>
    [ComponentCallback]
    public delegate void GameTimerEndedCallback(Arena arena);
}

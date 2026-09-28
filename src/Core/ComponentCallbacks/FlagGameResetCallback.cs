namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when the flag game is reset in an arena.
    /// </summary>
    /// <param name="arena">The arena the flag game was reset for.</param>
    /// <param name="winnerFreq">The team that won. -1 for no winner.</param>
    /// <param name="points">The # of points awarded to the winning team.</param>
    [ComponentCallback]
    public delegate void FlagGameResetCallback(Arena arena, short winnerFreq, int points);
}

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when the ball count is changed in an arena.
    /// </summary>
    /// <param name="arena">The arena the ball count changed for.</param>
    /// <param name="newCount">The previous # of balls.</param>
    /// <param name="oldCount">The new # of balls.</param>
    [ComponentCallback]
    public delegate void BallCountChangedCallback(Arena arena, int newCount, int oldCount);
}

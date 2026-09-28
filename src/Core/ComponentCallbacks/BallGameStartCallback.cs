namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback fired when a ball game (re)starts in an arena — when the first ball spawns after the game had ended
    /// (or after the ball count was set to 0). Fired by the <see cref="SS.Core.Modules.Balls"/> module.
    /// </summary>
    /// <param name="arena">The arena the game started in.</param>
    [ComponentCallback]
    public delegate void BallGameStartCallback(Arena arena);
}

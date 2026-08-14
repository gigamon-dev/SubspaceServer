namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback fired when a ball game (re)starts in an arena — when the first ball spawns after the game had ended
    /// (or after the ball count was set to 0). Fired by the <see cref="SS.Core.Modules.Balls"/> module.
    /// </summary>
    [CallbackHelper]
    public static partial class BallGameStartCallback
    {
        public delegate void BallGameStartDelegate(Arena arena);
    }
}

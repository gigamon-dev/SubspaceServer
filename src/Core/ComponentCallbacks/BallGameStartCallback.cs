namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback fired when a ball game becomes active in an arena (the first pickup of a scoring ball after the game was
    /// idle). Fired by the <see cref="SS.Core.Modules.Scoring.BallGamePoints"/> module.
    /// </summary>
    [CallbackHelper]
    public static partial class BallGameStartCallback
    {
        public delegate void BallGameStartDelegate(Arena arena);
    }
}

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback fired when a ball game ends in an arena (any <see cref="SS.Core.ComponentInterfaces.IBalls.EndGame"/>
    /// call — a team won, the timer ran out, or the game was reset). Fired by the <see cref="SS.Core.Modules.Balls"/>
    /// module.
    /// </summary>
    [CallbackHelper]
    public static partial class BallGameOverCallback
    {
        /// <param name="arena">The arena the game ended in.</param>
        /// <param name="winnerFreq">The winning freq (reported by the caller of <see cref="SS.Core.ComponentInterfaces.IBalls.EndGame"/>), or <c>-1</c> when the game ended with no single winner (timer, reset, etc.).</param>
        public delegate void BallGameOverDelegate(Arena arena, short winnerFreq);
    }
}

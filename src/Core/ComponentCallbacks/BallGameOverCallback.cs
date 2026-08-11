namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback fired when a ball game ends in an arena — either a team won or the game was reset. Fired by the
    /// <see cref="SS.Core.Modules.Scoring.BallGamePoints"/> module.
    /// </summary>
    [CallbackHelper]
    public static partial class BallGameOverCallback
    {
        /// <param name="arena">The arena the game ended in.</param>
        /// <param name="winnerFreq">The winning freq, or <c>-1</c> if the game was reset with no winner.</param>
        public delegate void BallGameOverDelegate(Arena arena, short winnerFreq);
    }
}

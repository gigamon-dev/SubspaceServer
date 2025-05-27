namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="FlagGameResetDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class FlagGameResetCallback
    {
        /// <summary>
        /// Delegate for when the flag game is reset in an arena.
        /// </summary>
        /// <param name="arena">The arena the flag game was reset for.</param>
        /// <param name="winnerFreq">The team that won. -1 for no winner.</param>
        /// <param name="points">The # of points awarded to the winning team.</param>
        public delegate void FlagGameResetDelegate(Arena arena, short winnerFreq, int points);        
    }
}

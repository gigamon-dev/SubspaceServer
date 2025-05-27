namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper for the <see cref="GameTimerEndedCallback"/> callback.
    /// </summary>
    /// <remarks>
    /// Also consider using the <see cref="GameTimerChangedCallback"/>.
    /// </remarks>
    [CallbackHelper]
    public static partial class GameTimerEndedCallback
    {
        /// <summary>
        /// Delegate for a callback when a game timer ends.
        /// </summary>
        /// <param name="arena">The arena.</param>
        public delegate void GameTimerEndedDelegate(Arena arena);
    }
}

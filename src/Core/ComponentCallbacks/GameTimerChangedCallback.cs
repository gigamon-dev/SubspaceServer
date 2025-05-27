namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Represents the type of change on a timer.
    /// </summary>
    public enum TimerChange
    {
        Started,
        Stopped,
        Paused,
        Unpaused,
    }

    /// <summary>
    /// The reason why a timer changed.
    /// </summary>
    public enum TimerChangeReason
    {
        /// <summary>
        /// Arena created or arena config changed.
        /// </summary>
        ArenaAction,

        /// <summary>
        /// A player issued a command.
        /// </summary>
        PlayerCommand,

        /// <summary>
        /// Another module called the interface
        /// </summary>
        InterfaceCall,

        /// <summary>
        /// The timer elapsed.
        /// </summary>
        Completion,
    }

    /// <summary>
    /// Helper for the <see cref="GameTimerChangedDelegate"/> callback.
    /// </summary>
    /// <remarks>
    /// It is possible to tell when a timer completes by watching for a reason of <see cref="TimerChangeReason.Completion"/>, 
    /// in which case the timer either got <see cref="TimerChange.Started"/> back up or <see cref="TimerChange.Stopped"/>.
    /// </remarks>
    [CallbackHelper]
    public static partial class GameTimerChangedCallback
    {
        public delegate void GameTimerChangedDelegate(Arena arena, TimerChange change, TimerChangeReason reason, bool isTimedGame);
    }
}

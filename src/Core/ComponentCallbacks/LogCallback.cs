using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="LogDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class LogCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a log is to be written.
        /// </summary>
        /// <param name="message">The log message to write.</param>
        public delegate void LogDelegate(ref readonly LogEntry logEntry);
    }
}

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="LogDroppedDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class LogDroppedCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a log entry is dropped (not written),
        /// due to the logging infrastructure having too much back pressure.
        /// </summary>
        /// <remarks>
        /// This is NOT executed on the mainloop thread.
        /// </remarks>
        /// <param name="totalDropped">The total # of log entries dropped.</param>
        public delegate void LogDroppedDelegate(int totalDropped);
    }
}

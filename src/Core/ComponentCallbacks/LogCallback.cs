using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Calllback delegate when a log is to be written.
    /// </summary>
    /// <param name="message">The log message to write.</param>
    [ComponentCallback]
    public delegate void LogCallback(ref readonly LogEntry logEntry);
}

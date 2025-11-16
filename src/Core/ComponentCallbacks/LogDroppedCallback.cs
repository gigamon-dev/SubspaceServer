namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate when a log entry is dropped (not written),
    /// due to the logging infrastructure having too much back pressure.
    /// </summary>
    /// <remarks>
    /// This is NOT executed on the mainloop thread.
    /// </remarks>
    /// <param name="totalDropped">The total # of log entries dropped.</param>
    [ComponentCallback]
    public delegate void LogDroppedCallback(int totalDropped);
}

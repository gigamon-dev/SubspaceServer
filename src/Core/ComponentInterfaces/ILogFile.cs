namespace SS.Core.ComponentInterfaces
{
    public interface ILogFile : IComponentInterface
    {
        /// <summary>
        /// Flushes the current log file to disk.
        /// </summary>
        void Flush();

        /// <summary>
        /// Closes and reopens the current log file.
        /// </summary>
        void Reopen();
    }
}

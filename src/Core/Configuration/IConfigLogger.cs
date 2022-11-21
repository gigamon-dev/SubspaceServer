using SS.Core.ComponentInterfaces;

namespace SS.Core.Configuration
{
    /// <summary>
    /// Interface that the <see cref="SS.Core.Configuration"/> functionality uses to log information related to configuration.
    /// </summary>
    public interface IConfigLogger
    {
        void Log(LogLevel level, string message);
    }
}

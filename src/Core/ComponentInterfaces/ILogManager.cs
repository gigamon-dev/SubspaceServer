using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public enum LogLevel
    {
        Drivel = 1,
        Info,
        Malicious,
        Warn,
        Error,
    }

    public enum LogCode
    {
        D = 1,
        I,
        M,
        W,
        E,
    }

    public interface ILogManager : IComponentInterface
    {
        /// <summary>
        /// Adds a line to the server log.
        /// Lines should look like:
        /// <example>"&lt;module&gt; {arena} [player] did something"</example>
        /// </summary>
        /// <param name="level"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        void Log(LogLevel level, string format, params object[] args);

        /// <summary>
        /// Adds a line to the server log, specialized for arena-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="arena"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        void LogA(LogLevel level, string module, Arena arena, string format, params object[] args);

        /// <summary>
        /// Adds a line to the server log, specialized for player-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="player"></param>
        /// <param name="format"></param>
        /// <param name="args"></param>
        void LogP(LogLevel level, string module, Player player, string format, params object[] args);

        /// <summary>
        /// Determines if a specific message should be logged by a specific module.
        /// </summary>
        /// <remarks>Log handlers can optionally call this function to support
        /// filtering of the log messages that go through them. The filters
        /// are be defined by an administrator in global.conf.</remarks>
        /// <param name="line">the log line that was received by the log handler</param>
        /// <param name="logModuleName">the module name of the log handler (e.g., log_file)</param>
        /// <returns>true if the message should be logged, otherwise false</returns>
        bool FilterLog(string line, string logModuleName);
    }
}

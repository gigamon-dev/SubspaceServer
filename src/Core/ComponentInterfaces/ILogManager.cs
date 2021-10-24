using System;
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

    public static class LogLevelExtensions
    {
        public static char ToChar(this LogLevel level) => level switch
        {
            LogLevel.Drivel => 'D',
            LogLevel.Info => 'I',
            LogLevel.Malicious => 'M',
            LogLevel.Warn => 'W',
            LogLevel.Error => 'E',
            _ => '?'
        };
    }

    public struct LogEntry
    {
        public LogLevel Level;
        public string Module;
        public Arena Arena;
        public Player Player;
        public StringBuilder LogText;
    }

    public interface ILogManager : IComponentInterface
    {
        /// <summary>
        /// Adds a line to the server log.
        /// Lines should look like:
        /// <example>"&lt;module&gt; {arena} [player] did something"</example>
        /// </summary>
        /// <param name="level"></param>
        /// <param name="message"></param>
        void Log(LogLevel level, ReadOnlySpan<char> message);

        /// <summary>
        /// Adds a line to the server log.
        /// Lines should look like:
        /// <example>"&lt;module&gt; {arena} [player] did something"</example>
        /// </summary>
        /// <param name="level"></param>
        /// <param name="message"></param>
        void Log(LogLevel level, StringBuilder message);

        /// <summary>
        /// Adds a line to the serer log, specialized for module-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="message"></param>
        void LogM(LogLevel level, string module, ReadOnlySpan<char> message);

        /// <summary>
        /// Adds a line to the sevrer log, specialized for module-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="message"></param>
        void LogM(LogLevel level, string module, StringBuilder message);

        /// <summary>
        /// Adds a line to the server log, specialized for arena-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="arena"></param>
        /// <param name="message"></param>
        void LogA(LogLevel level, string module, Arena arena, ReadOnlySpan<char> message);

        /// <summary>
        /// Adds a line to the server log, specialized for arena-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="arena"></param>
        /// <param name="message"></param>
        void LogA(LogLevel level, string module, Arena arena, StringBuilder message);

        /// <summary>
        /// Adds a line to the server log, specialized for player-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="player"></param>
        /// <param name="message"></param>
        void LogP(LogLevel level, string module, Player player, ReadOnlySpan<char> message);

        /// <summary>
        /// Adds a line to the server log, specialized for player-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="player"></param>
        /// <param name="message"></param>
        void LogP(LogLevel level, string module, Player player, StringBuilder message);

        /// <summary>
        /// Determines if a specific message should be logged by a specific module.
        /// </summary>
        /// <remarks>Log handlers can optionally call this function to support
        /// filtering of the log messages that go through them. The filters
        /// are be defined by an administrator in global.conf.</remarks>
        /// <param name="line">the log line that was received by the log handler</param>
        /// <param name="logModuleName">the module name of the log handler (e.g., log_file)</param>
        /// <returns>true if the message should be logged, otherwise false</returns>
        bool FilterLog(in LogEntry logEntry, string logModuleName);
    }
}

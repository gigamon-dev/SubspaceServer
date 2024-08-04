using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Severity levels of logs.
    /// </summary>
    public enum LogLevel
    {
        /// <summary>
        /// Unimportant info. Useful for investigation during development.
        /// </summary>
        Drivel = 1,

        /// <summary>
        /// Informative info.
        /// </summary>
        Info,

        /// <summary>
        /// Info about a event that is potentially malicious, usually about the client-side misbehaving.
        /// </summary>
        Malicious,

        /// <summary>
        /// Info about an abnormal or unexpected event, of low severity.
        /// </summary>
        Warn,

        /// <summary>
        /// Info about a failure.
        /// </summary>
        Error,
    }

    /// <summary>
    /// Log codes for <see cref="LogLevel"/>.
    /// </summary>
    public enum LogCode
    {
        /// <summary><see cref="LogLevel.Drivel"/></summary>
        D = 1,

        /// <summary><see cref="LogLevel.Info"/></summary>
        I,

        /// <summary><see cref="LogLevel.Malicious"/></summary>
        M,

        /// <summary><see cref="LogLevel.Warn"/></summary>
        W,

        /// <summary><see cref="LogLevel.Error"/></summary>
        E,
    }

    public static class LogLevelExtensions
    {
        /// <summary>
        /// Converts a <see cref="LogLevel"/> to a character code used in the default logging format.
        /// </summary>
        /// <param name="level"></param>
        /// <returns></returns>
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

    /// <summary>
    /// Information for a single log entry.
    /// </summary>
    public readonly struct LogEntry
    {
        /// <summary>
        /// The severity of the log.
        /// </summary>
        public readonly required LogLevel Level { get; init; }

        /// <summary>
        /// The name of the module that the log originate from.
        /// </summary>
        public readonly string Module { get; init; }

        /// <summary>
        /// The arena the log is related to.
        /// </summary>
        public readonly Arena Arena { get; init; }

        /// <summary>
        /// The name of the player the log is related to.
        /// </summary>
        public readonly string PlayerName { get; init; }

        /// <summary>
        /// The ID of the player the log is related to.
        /// </summary>
        public readonly int? PlayerId { get; init; }

        /// <summary>
        /// Text of the log, in the format:
        /// <code>LogCode &lt;module&gt; {arena} [player] message</code>
        /// LogCode is a single character representing the <see cref="Level"/>.
        /// Module, arena, and player are optional.
        /// </summary>
        public readonly required StringBuilder LogText { get; init; }
    }

    /// <summary>
    /// Interface for a logging service.
    /// </summary>
    /// <remarks>
    /// Use the LogM, LogA, and LogP methods to automatically format a log entry.
    /// </remarks>
    public interface ILogManager : IComponentInterface
    {
        #region Log

        /// <summary>
        /// Writes a log entry.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="handler">
        /// The message to write, in the format:
        /// <code>"&lt;module&gt; {arena} [player] did something"</code>
        /// Module, arena, and player are optional.
        /// </param>
        void Log(LogLevel level, [InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Writes a log entry.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        /// <param name="handler">
        /// The message to write, in the format:
        /// <code>"&lt;module&gt; {arena} [player] did something"</code>
        /// Module, arena, and player are optional.
        /// </param>
        void Log(LogLevel level, IFormatProvider provider, [InterpolatedStringHandlerArgument("", nameof(provider))] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Writes a log entry.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="message">
        /// The message to write, in the format:
        /// <code>"&lt;module&gt; {arena} [player] did something"</code>
        /// Module, arena, and player are optional.
        /// </param>
        void Log(LogLevel level, ReadOnlySpan<char> message);

        /// <summary>
        /// Writes a log entry.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="message">
        /// The message to write, in the format:
        /// <code>"&lt;module&gt; {arena} [player] did something"</code>
        /// Module, arena, and player are optional.
        /// </param>
        void Log(LogLevel level, string message);

        /// <summary>
        /// Writes a log entry.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="message">
        /// The message to write, in the format:
        /// <code>"&lt;module&gt; {arena} [player] did something"</code>
        /// Module, arena, and player are optional.
        /// </param>
        void Log(LogLevel level, StringBuilder message);

        #endregion

        #region Log Module

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="handler">The message to write.</param>
        void LogM(LogLevel level, string module, [InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        /// <param name="handler">The message to write.</param>
        void LogM(LogLevel level, string module, IFormatProvider provider, [InterpolatedStringHandlerArgument("", nameof(provider))] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="message">The message to write.</param>
        void LogM(LogLevel level, string module, ReadOnlySpan<char> message);

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="message">The message to write.</param>
        void LogM(LogLevel level, string module, string message);

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="message">The message to write.</param>
        void LogM(LogLevel level, string module, StringBuilder message);

        #endregion

        #region Log Arena

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/> and <paramref name="arena"/>.
        /// Adds a line to the server log, specialized for arena-specific messages.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="arena">The arena that the log entry is associated with.</param>
        /// <param name="handler">The message to write.</param>
        void LogA(LogLevel level, string module, Arena arena, [InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/> and <paramref name="arena"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="arena">The arena that the log entry is associated with.</param>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        /// <param name="handler">The message to write.</param>
        void LogA(LogLevel level, string module, Arena arena, IFormatProvider provider, [InterpolatedStringHandlerArgument("", nameof(provider))] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/> and <paramref name="arena"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="arena">The arena that the log entry is associated with.</param>
        /// <param name="message">The message to write.</param>
        void LogA(LogLevel level, string module, Arena arena, ReadOnlySpan<char> message);

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/> and <paramref name="arena"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="arena">The arena that the log entry is associated with.</param>
        /// <param name="message">The message to write.</param>
        void LogA(LogLevel level, string module, Arena arena, string message);

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/> and <paramref name="arena"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="arena">The arena that the log entry is associated with.</param>
        /// <param name="message">The message to write.</param>
        void LogA(LogLevel level, string module, Arena arena, StringBuilder message);

        #endregion

        #region Log Player

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/> and <paramref name="player"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="player">The player that the log entry is associated with.</param>
        /// <param name="handler">The message to write.</param>
        void LogP(LogLevel level, string module, Player player, [InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/> and <paramref name="player"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="player">The player that the log entry is associated with.</param>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        /// <param name="handler">The message to write.</param>
        void LogP(LogLevel level, string module, Player player, IFormatProvider provider, [InterpolatedStringHandlerArgument("", nameof(provider))] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/> and <paramref name="player"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="player">The player that the log entry is associated with.</param>
        /// <param name="message">The message to write.</param>
        void LogP(LogLevel level, string module, Player player, ReadOnlySpan<char> message);

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/> and <paramref name="player"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="player">The player that the log entry is associated with.</param>
        /// <param name="message">The message to write.</param>
        void LogP(LogLevel level, string module, Player player, string message);

        /// <summary>
        /// Writes a log entry for a specified <paramref name="module"/> and <paramref name="player"/>.
        /// </summary>
        /// <param name="level">The severity of the log to write.</param>
        /// <param name="module">The name of the module writing the log.</param>
        /// <param name="player">The player that the log entry is associated with.</param>
        /// <param name="message">The message to write.</param>
        void LogP(LogLevel level, string module, Player player, StringBuilder message);

        #endregion

        /// <summary>
        /// Checks whether a log entry should be logged by a specific handler.
        /// </summary>
        /// <remarks>
        /// Log handlers can optionally call this function to support filtering of the log entries that they're processing. 
        /// The filters are be defined by an administrator in global.conf.
        /// <para>
        /// The <paramref name="logModuleName"/>s match the names used in ASSS, to stay compatible with ASSS conf files.
        /// </para>
        /// </remarks>
        /// <param name="logEntry">The log entry to check.</param>
        /// <param name="logModuleName">The module name of the log handler (e.g., log_file).</param>
        /// <returns><see langword="true"/> if the message should be logged, otherwise <see langword="false"/>.</returns>
        bool FilterLog(ref readonly LogEntry logEntry, string logModuleName);
    }
}

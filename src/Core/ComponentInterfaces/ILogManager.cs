using Microsoft.Extensions.ObjectPool;
using System;
using System.Runtime.CompilerServices;
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
        public string PlayerName;
        public int? PlayerId;
        public StringBuilder LogText;
    }

    public interface ILogManager : IComponentInterface
    {
        #region Log

        /// <summary>
        /// Adds a line to the server log.
        /// Lines should look like:
        /// <example>"&lt;module&gt; {arena} [player] did something"</example>
        /// </summary>
        /// <param name="level"></param>
        /// <param name="handler">The interpolated string.</param>
        void Log(LogLevel level, [InterpolatedStringHandlerArgument("")] ref LogManagerInterpolatedStringHandler handler);

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
        void Log(LogLevel level, string message);

        /// <summary>
        /// Adds a line to the server log.
        /// Lines should look like:
        /// <example>"&lt;module&gt; {arena} [player] did something"</example>
        /// </summary>
        /// <param name="level"></param>
        /// <param name="message"></param>
        void Log(LogLevel level, StringBuilder message);

        #endregion

        #region Log Module

        /// <summary>
        /// Adds a line to the server log, specialized for module-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="handler">The interpolated string.</param>
        void LogM(LogLevel level, string module, [InterpolatedStringHandlerArgument("")] ref LogManagerInterpolatedStringHandler handler);

        /// <summary>
        /// Adds a line to the server log, specialized for module-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="message"></param>
        void LogM(LogLevel level, string module, ReadOnlySpan<char> message);

        /// <summary>
        /// Adds a line to the server log, specialized for module-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="message"></param>
        void LogM(LogLevel level, string module, string message);

        /// <summary>
        /// Adds a line to the server log, specialized for module-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="message"></param>
        void LogM(LogLevel level, string module, StringBuilder message);

        #endregion

        #region Log Arena

        /// <summary>
        /// Adds a line to the server log, specialized for arena-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="arena"></param>
        /// <param name="handler">The interpolated string.</param>
        void LogA(LogLevel level, string module, Arena arena, [InterpolatedStringHandlerArgument("")] ref LogManagerInterpolatedStringHandler handler);

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
        void LogA(LogLevel level, string module, Arena arena, string message);

        /// <summary>
        /// Adds a line to the server log, specialized for arena-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="arena"></param>
        /// <param name="message"></param>
        void LogA(LogLevel level, string module, Arena arena, StringBuilder message);

        #endregion

        #region Log Player

        /// <summary>
        /// Adds a line to the server log, specialized for player-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="player"></param>
        /// <param name="handler">The interpolated string.</param>
        void LogP(LogLevel level, string module, Player player, [InterpolatedStringHandlerArgument("")] ref LogManagerInterpolatedStringHandler handler);

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
        void LogP(LogLevel level, string module, Player player, string message);

        /// <summary>
        /// Adds a line to the server log, specialized for player-specific messages.
        /// </summary>
        /// <param name="level"></param>
        /// <param name="module"></param>
        /// <param name="player"></param>
        /// <param name="message"></param>
        void LogP(LogLevel level, string module, Player player, StringBuilder message);

        #endregion

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

        /// <summary>
        /// Pool of StringBuilder objects.
        /// </summary>
        /// <remarks>
        /// Only for use by <see cref="LogManagerInterpolatedStringHandler"/>.
        /// </remarks>
        ObjectPool<StringBuilder> StringBuilderPool { get; } // TODO: Maybe move this to another interface, IStringBuilderPoolProvider, and cast ILogManager to it?
    }

    [InterpolatedStringHandler]
    public struct LogManagerInterpolatedStringHandler
    {
        private readonly ILogManager _logManager;
        private StringBuilder _stringBuilder;
        private StringBuilder.AppendInterpolatedStringHandler _wrappedHandler;

        public LogManagerInterpolatedStringHandler(int literalLength, int formatCount, ILogManager logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _stringBuilder = _logManager.StringBuilderPool.Get();
            _wrappedHandler = new StringBuilder.AppendInterpolatedStringHandler(literalLength, formatCount, _stringBuilder);
        }

        public LogManagerInterpolatedStringHandler(int literalLength, int formatCount, ILogManager logManager, IFormatProvider provider)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _stringBuilder = _logManager.StringBuilderPool.Get();
            _wrappedHandler = new StringBuilder.AppendInterpolatedStringHandler(literalLength, formatCount, _stringBuilder, provider);
        }

        public void AppendLiteral(string value)
        {
            _wrappedHandler.AppendLiteral(value);
        }

        #region AppendFormatted

        #region AppendFormatted T

        public void AppendFormatted<T>(T value) => _wrappedHandler.AppendFormatted<T>(value);

        public void AppendFormatted<T>(T value, string format) => _wrappedHandler.AppendFormatted<T>(value, format);

        public void AppendFormatted<T>(T value, int alignment) => _wrappedHandler.AppendFormatted<T>(value, alignment);

        public void AppendFormatted<T>(T value, int alignment, string format) => _wrappedHandler.AppendFormatted<T>(value, alignment, format);

        #endregion

        #region AppendFormatted ReadOnlySpan<char>

        public void AppendFormatted(ReadOnlySpan<char> value) => _wrappedHandler.AppendFormatted(value);

        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string format = null) => _wrappedHandler.AppendFormatted(value, alignment, format);

        #endregion

        #region AppendFormatted string

        public void AppendFormatted(string value) => _wrappedHandler.AppendFormatted(value);

        public void AppendFormatted(string value, int alignment = 0, string format = null) => _wrappedHandler.AppendFormatted(value, alignment, format);

        #endregion

        #region AppendFormatted object

        public void AppendFormatted(object value, int alignment = 0, string format = null) => _wrappedHandler.AppendFormatted(value, alignment, format);

        #endregion

        #endregion

        public void CopyToAndClear(StringBuilder destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            if (_stringBuilder != null)
            {
                destination.Append(_stringBuilder);
                _logManager.StringBuilderPool.Return(_stringBuilder);
                _stringBuilder = null;
                _wrappedHandler = default;
            }
        }
    }
}

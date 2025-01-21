using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.IO;
using System.Text;
using System.Threading;
using LogSettings = SS.Core.ConfigHelp.Constants.Global.Log;

namespace SS.Core.Modules
{
    /// <summary>
    /// Logging module that writes to a file.
    /// </summary>
    [CoreModuleInfo]
    public sealed class LogFile : IModule, ILogFile
    {
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IServerTimer _serverTimer;
        private InterfaceRegistrationToken<ILogFile>? _iLogFileToken;

        private readonly Lock _lock = new();
        private StreamWriter? _streamWriter = null;
        private DateTime? _fileDate;

        public LogFile(
            IConfigManager configManager,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager,
            IServerTimer serverTimer)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _serverTimer = serverTimer ?? throw new ArgumentNullException(nameof(serverTimer));
        }

        #region Module methods

        [ConfigHelp<int>("Log", "FileFlushPeriod", ConfigScope.Global, Default = 10,
            Description = "How often to flush the log file to disk (in minutes).")]
        bool IModule.Load(IComponentBroker broker)
        {
            ReopenLog();

            LogCallback.Register(broker, Callback_Log);

            int flushMinutes = _configManager.GetInt(_configManager.Global, "Log", "FileFlushPeriod", LogSettings.FileFlushPeriod.Default);
            int flushMilliseconds = (int)TimeSpan.FromMinutes(flushMinutes).TotalMilliseconds;
            _serverTimer.SetTimer(ServerTimer_FlushLog, flushMilliseconds, flushMilliseconds, null);

            _iLogFileToken = broker.RegisterInterface<ILogFile>(this);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iLogFileToken) != 0)
                return false;

            _serverTimer.ClearTimer(ServerTimer_FlushLog, null);

            LogCallback.Unregister(broker, Callback_Log);

            CloseLog();

            return true;
        }

        #endregion

        #region ILogFile

        void ILogFile.Flush()
        {
            FlushLog();
        }

        void ILogFile.Reopen()
        {
            ReopenLog();
        }

        #endregion

        private void Callback_Log(ref readonly LogEntry logEntry)
        {
            if (!_logManager.FilterLog(in logEntry, "log_file"))
                return;

            lock (_lock)
            {
                if (_streamWriter is null)
                    return;

                DateTime now = DateTime.UtcNow;

                if (_fileDate != now.Date)
                {
                    ReopenLog();

                    if (_streamWriter is null)
                        return;
                }

                try
                {
                    Span<char> dateTimeStr = stackalloc char[20];
                    if (now.TryFormat(dateTimeStr, out int charsWritten, "u"))
                    {
                        _streamWriter.Write(dateTimeStr[..charsWritten]);
                        _streamWriter.Write(' ');
                    }

                    _streamWriter.Write(logEntry.LogText);
                    _streamWriter.WriteLine();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error writing to log file. {ex}");
                }
            }
        }

        private bool ServerTimer_FlushLog()
        {
            FlushLog();
            return true;
        }

        private void FlushLog()
        {
            lock (_lock)
            {
                try
                {
                    _streamWriter?.Flush();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error flushing log file writer. {ex}");
                }
            }
        }

        private void CloseLog()
        {
            lock (_lock)
            {
                if (_streamWriter is not null)
                {
                    try
                    {
                        _streamWriter.Close();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error closing log file. {ex}");
                    }

                    _streamWriter = null;
                }
            }
        }

        [ConfigHelp("Log", "DatedLogsPath", ConfigScope.Global, Default = "log", Description = "Path of the folder to store logs.")]
        private void ReopenLog()
        {
            lock (_lock)
            {
                CloseLog();

                // TODO: add logic to clean up old log files based on a config setting

                string? path = _configManager.GetStr(_configManager.Global, "Log", "DatedLogsPath");
                if (string.IsNullOrWhiteSpace(path))
                    path = LogSettings.DatedLogsPath.Default;

                _fileDate = DateTime.UtcNow.Date;
                string fileName = $"{_fileDate:yyyy-MM-dd}.log";

                try
                {
                    _streamWriter = new StreamWriter(Path.Combine(path, fileName), true, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error opening log file '{fileName}'. {ex}");
                    _fileDate = null;
                    return;
                }

                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                try
                {
                    sb.Append($"I <{nameof(LogFile)}> Opening log file ==================================");

                    LogEntry logEntry = new()
                    {
                        Level = LogLevel.Info,
                        Module = nameof(LogFile),
                        LogText = sb,
                    };

                    Callback_Log(ref logEntry);
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }
        }
    }
}

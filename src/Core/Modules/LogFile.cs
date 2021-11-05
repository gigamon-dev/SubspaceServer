using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.IO;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Logging module that writes to a file.
    /// </summary>
    public class LogFile : IModule, ILogFile
    {
        private IConfigManager _configManager;
        private ILogManager _logManager;
        IObjectPoolManager _objectPoolManager;
        private IServerTimer _serverTimer;
        private InterfaceRegistrationToken _iLogFileToken;

        private readonly object _lockObj = new();
        private StreamWriter _streamWriter = null;
        private DateTime? _fileDate;

        #region Module methods

        public bool Load(
            ComponentBroker broker,
            IConfigManager configManager,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager,
            IServerTimer serverTimer)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _serverTimer = serverTimer ?? throw new ArgumentNullException(nameof(serverTimer));

            ReopenLog();

            LogCallback.Register(broker, Callback_Log);

            int flushMinutes = _configManager.GetInt(_configManager.Global, "Log", "FileFlushPeriod", 10);
            int flushMilliseconds = (int)TimeSpan.FromMinutes(flushMinutes).TotalMilliseconds;
            _serverTimer.SetTimer(ServerTimer_FlushLog, flushMilliseconds, flushMilliseconds, null);

            _iLogFileToken = broker.RegisterInterface<ILogFile>(this);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface<ILogFile>(ref _iLogFileToken);

            _serverTimer.ClearTimer(ServerTimer_FlushLog, null);

            LogCallback.Unregister(broker, Callback_Log);

            lock (_lockObj)
            {
                if (_streamWriter != null)
                {
                    _streamWriter.Close();
                    _streamWriter = null;
                }
            }

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

        private void Callback_Log(in LogEntry logEntry)
        {
            if (!_logManager.FilterLog(in logEntry, "log_file"))
                return;
            
            lock (_lockObj)
            {
                if (_streamWriter == null)
                    return;

                DateTime now = DateTime.UtcNow;

                if (_fileDate != now.Date)
                {
                    ReopenLog();

                    if (_streamWriter == null)
                        return;
                }

                Span<char> dateTimeStr = stackalloc char[20];
                if (now.TryFormat(dateTimeStr, out int charsWritten, "u"))
                {
                    _streamWriter.Write(dateTimeStr.Slice(0, charsWritten));
                    _streamWriter.Write(' ');
                }

                _streamWriter.Write(logEntry.LogText);
                _streamWriter.WriteLine();
            }
        }

        private bool ServerTimer_FlushLog()
        {
            FlushLog();
            return true;
        }

        private void FlushLog()
        {
            lock (_lockObj)
            {
                _streamWriter?.Flush();
            }
        }

        private void ReopenLog()
        {
            lock (_lockObj)
            {
                if (_streamWriter != null)
                {
                    _streamWriter.Close();
                    _streamWriter = null;
                }

                // TODO: add logic to clean up old log files based on a config setting

                string path = _configManager.GetStr(_configManager.Global, "Log", "DatedLogsPath");
                if (string.IsNullOrWhiteSpace(path))
                    path = "log";

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
                    sb.Append("I <LogFile> Opening log file ==================================");

                    Callback_Log(
                        new LogEntry()
                        {
                            LogText = sb,
                        });
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }
        }
    }
}

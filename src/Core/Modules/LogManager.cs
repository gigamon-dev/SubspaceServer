using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class LogManager : IModule, IModuleLoaderAware, ILogManager
    {
        private ComponentBroker _broker;
        private InterfaceRegistrationToken iLogManagerToken;

        private readonly MessagePassingQueue<string> _logQueue = new();
        private Thread _loggingThread;

        private readonly ReaderWriterLockSlim _rwLock = new();
        private IConfigManager _configManager = null;

        private void LoggingThread()
        {
            string message;

            while (true)
            {
                message = _logQueue.Dequeue();

                if (message == null)
                    return;

                LogCallback.Fire(_broker, message);
            }
        }

        #region IModule Members

        public bool Load(ComponentBroker broker)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            iLogManagerToken = broker.RegisterInterface<ILogManager>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface<ILogManager>(ref iLogManagerToken) != 0)
                return false;

            _logQueue.Enqueue(null);
            _loggingThread?.Join();
            return true;
        }

        #endregion

        #region IModuleLoaderAware Members

        bool IModuleLoaderAware.PostLoad(ComponentBroker broker)
        {
            _rwLock.EnterWriteLock();

            try
            {
                _configManager = broker.GetInterface<IConfigManager>();
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            _loggingThread = new Thread(new ThreadStart(LoggingThread));
            _loggingThread.Name = nameof(LogManager);
            _loggingThread.Start();

            return true;
        }

        bool IModuleLoaderAware.PreUnload(ComponentBroker broker)
        {
            _rwLock.EnterWriteLock();

            try
            {
                if (_configManager != null)
                    broker.ReleaseInterface(ref _configManager);

                return true;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        #endregion

        #region ILogManager Members

        void ILogManager.Log(LogLevel level, string format, params object[] args)
        {
            StringBuilder sb = new();
            sb.Append(((LogCode)level).ToString());
            sb.Append(' ');

            if (args != null && args.Length > 0)
                sb.AppendFormat(format, args);
            else
                sb.Append(format);

            _logQueue.Enqueue(sb.ToString());
        }

        void ILogManager.LogM(LogLevel level, string module, string format, params object[] args)
        {
            StringBuilder sb = new();
            sb.Append(((LogCode)level).ToString());
            sb.Append(" <");
            sb.Append(module);
            sb.Append("> ");
            sb.AppendFormat(format, args);

            _logQueue.Enqueue(sb.ToString());
        }

        void ILogManager.LogA(LogLevel level, string module, Arena arena, string format, params object[] args)
        {
            StringBuilder sb = new();
            sb.Append(((LogCode)level).ToString());
            sb.Append(" <");
            sb.Append(module);
            sb.Append("> {");
            sb.Append(arena?.Name ?? "(bad arena)");
            sb.Append("} ");
            sb.AppendFormat(format, args);

            _logQueue.Enqueue(sb.ToString());
        }

        void ILogManager.LogP(LogLevel level, string module, Player player, string format, params object[] args)
        {
            Arena arena = player?.Arena;

            StringBuilder sb = new();
            sb.Append(((LogCode)level).ToString());
            sb.Append(" <");
            sb.Append(module);
            sb.Append("> {");
            sb.Append(arena?.Name ?? "(bad arena)");
            sb.Append("} [");
            sb.Append(player?.Name ?? ((player != null) ? "pid=" + player.Id : null) ?? "(null player)");
            sb.Append("] ");
            sb.AppendFormat(format, args);

            _logQueue.Enqueue(sb.ToString());
        }

        bool ILogManager.FilterLog(string line, string logModuleName)
        {
            if (string.IsNullOrEmpty(line))
                return true;

            if (string.IsNullOrEmpty(logModuleName))
                return true;

            _rwLock.EnterReadLock();

            try
            {
                if (_configManager == null)
                    return true; // filtering disabled

                string origin = null;

                int startIndex = line.IndexOf('<');
                if (startIndex != -1)
                {
                    int endIndex = line.IndexOf('>', startIndex + 1);
                    if (endIndex != -1)
                    {
                        int originLength = endIndex - startIndex - 1;
                        if (originLength > 0)
                            origin = line.Substring(startIndex + 1, originLength);
                    }
                }

                if (string.IsNullOrEmpty(origin))
                    origin = "unknown";

                string settingValue = _configManager.GetStr(_configManager.Global, logModuleName, origin);
                if (settingValue == null)
                {
                    settingValue = _configManager.GetStr(_configManager.Global, logModuleName, "all");
                    if (settingValue == null)
                        return true; // filtering disabled
                }

                if (!settingValue.Contains(line[0]))
                    return false;

                return true;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        #endregion
    }
}

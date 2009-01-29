using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using SS.Utilities;
using SS.Core.ComponentInterfaces;
using SS.Core.ComponentCallbacks;

namespace SS.Core.Modules
{
    public class LogManager : IModule, IModuleLoaderAware, ILogManager
    {
        private MessagePassingQueue<string> _logQueue = new MessagePassingQueue<string>();
        private Thread _loggingThread;

        private ModuleManager _mm;

        private ReaderWriterLock _moduleUnloadLock = new ReaderWriterLock(); // using rwlock in case we ever have multiple logging threads
        private IConfigManager _configManager = null;

        private void loggingThread()
        {
            string message;

            while (true)
            {
                message = _logQueue.Dequeue();

                if (message == null)
                    return;

                LogCallback.Fire(_mm, message);
            }
        }

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get { return null; }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;
            mm.RegisterInterface<ILogManager>(this);
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            if (mm.UnregisterInterface<ILogManager>() != 0)
                return false;

            _logQueue.Enqueue(null);
            _loggingThread.Join();
            return true;
        }

        #endregion

        #region IModuleLoaderAware Members

        bool IModuleLoaderAware.PostLoad(ModuleManager mm)
        {
            _configManager = mm.GetInterface<IConfigManager>();
            _loggingThread = new Thread(new ThreadStart(loggingThread));
            _loggingThread.Start();
            return true;
        }

        bool IModuleLoaderAware.PreUnload(ModuleManager mm)
        {
            _moduleUnloadLock.AcquireWriterLock(Timeout.Infinite);

            try
            {
                mm.ReleaseInterface<IConfigManager>();
                _configManager = null;

                return true;
            }
            finally
            {
                _moduleUnloadLock.ReleaseWriterLock();
            }
        }

        #endregion

        #region ILogManager Members

        void ILogManager.Log(LogLevel level, string format, params object[] args)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(((LogCode)level).ToString());
            sb.Append(' ');

            if (args != null && args.Length > 0)
                sb.Append(string.Format(format, args));
            else
                sb.Append(format);

            _logQueue.Enqueue(sb.ToString());
        }

        void ILogManager.LogA(LogLevel level, string module, Arena arena, string format, params object[] args)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(((LogCode)level).ToString());
            sb.Append(" <");
            sb.Append(module);
            sb.Append("> {");
            sb.Append((arena != null) ? arena.Name : "(bad arena)");
            sb.Append("} ");
            sb.Append(string.Format(format, args));

            _logQueue.Enqueue(sb.ToString());
        }

        void ILogManager.LogP(LogLevel level, string module, Player player, string format, params object[] args)
        {
            Arena arena = player != null ? player.Arena : null;

            StringBuilder sb = new StringBuilder();
            sb.Append(((LogCode)level).ToString());
            sb.Append(" <");
            sb.Append(module);
            sb.Append("> {");
            sb.Append((arena != null) ? arena.Name : "(bad arena)");
            sb.Append("} [");
            sb.Append(player != null ? (player.Name != null) ? player.Name : player.Id.ToString() : "(bad player)");
            sb.Append("] ");
            sb.Append(string.Format(format, args));

            _logQueue.Enqueue(sb.ToString());
        }

        bool ILogManager.FilterLog(string line, string logModuleName)
        {
            if (string.IsNullOrEmpty(line))
                return true;

            if (string.IsNullOrEmpty(logModuleName))
                return true;

            _moduleUnloadLock.AcquireReaderLock(Timeout.Infinite);

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
                        origin = line.Substring(startIndex + 1, endIndex - startIndex);
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

                if (settingValue.IndexOf(line[0]) == -1)
                    return false;

                return true;
            }
            finally
            {
                _moduleUnloadLock.ReleaseReaderLock();
            }
        }

        #endregion
    }
}
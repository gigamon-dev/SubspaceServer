using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class LogManager : IModule, IModuleLoaderAware, ILogManager
    {
        private ComponentBroker _broker;
        private IObjectPoolManager _objectPoolManager;
        private InterfaceRegistrationToken iLogManagerToken;

        private NonTransientObjectPool<StringBuilder> _stringBuilderPool;
        private readonly BlockingCollection<LogEntry> _logQueue = new(512);
        private Thread _loggingThread;

        private readonly ReaderWriterLockSlim _rwLock = new();
        private IConfigManager _configManager = null;

        private void LoggingThread()
        {
            LogEntry logEntry;

            while (!_logQueue.IsCompleted)
            {
                try
                {
                    logEntry = _logQueue.Take();
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                try
                {
                    LogCallback.Fire(_broker, logEntry);
                }
                finally
                {
                    _stringBuilderPool.Return(logEntry.LogText);
                }
            }
        }

        #region IModule Members

        public bool Load(ComponentBroker broker, IObjectPoolManager objectPoolManager)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            _stringBuilderPool = new NonTransientObjectPool<StringBuilder>(
                new StringBuilderPooledObjectPolicy()
                {
                    InitialCapacity = 1024,
                    MaximumRetainedCapacity = 4096,
                }
            );
            _objectPoolManager.TryAddTracked(_stringBuilderPool);

            iLogManagerToken = broker.RegisterInterface<ILogManager>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface<ILogManager>(ref iLogManagerToken) != 0)
                return false;

            _logQueue.CompleteAdding();
            _loggingThread?.Join();

            _objectPoolManager.TryRemoveTracked(_stringBuilderPool);

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

            _loggingThread = new Thread(new ThreadStart(LoggingThread))
            {
                Name = nameof(LogManager)
            };
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

        void ILogManager.Log(LogLevel level, ReadOnlySpan<char> message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            sb.Append(level.ToChar());
            sb.Append(' ');
            sb.Append(message);

            try
            {
                _logQueue.Add(
                    new LogEntry()
                    {
                        Level = level,
                        LogText = sb,
                    });
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.Log(LogLevel level, StringBuilder message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            sb.Append(level.ToChar());
            sb.Append(' ');
            sb.Append(message);

            try
            {
                _logQueue.Add(
                    new LogEntry()
                    {
                        Level = level,
                        LogText = sb,
                    });
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogM(LogLevel level, string module, ReadOnlySpan<char> message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            sb.Append(level.ToChar());
            sb.Append(" <");
            sb.Append(module);
            sb.Append("> ");
            sb.Append(message);

            try
            {
                _logQueue.Add(
                    new LogEntry()
                    {
                        Level = level,
                        Module = module,
                        LogText = sb,
                    });
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogM(LogLevel level, string module, StringBuilder message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            sb.Append(level.ToChar());
            sb.Append(" <");
            sb.Append(module);
            sb.Append("> ");
            sb.Append(message);

            try
            {
                _logQueue.Add(
                    new LogEntry()
                    {
                        Level = level,
                        Module = module,
                        LogText = sb,
                    });
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogA(LogLevel level, string module, Arena arena, ReadOnlySpan<char> message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            sb.Append(level.ToChar());
            sb.Append(" <");
            sb.Append(module);
            sb.Append("> {");
            sb.Append(arena?.Name ?? "(no arena)");
            sb.Append("} ");
            sb.Append(message);

            try
            {
                _logQueue.Add(
                    new LogEntry()
                    {
                        Level = level,
                        Module = module,
                        Arena = arena,
                        LogText = sb,
                    });
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogA(LogLevel level, string module, Arena arena, StringBuilder message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            sb.Append(level.ToChar());
            sb.Append(" <");
            sb.Append(module);
            sb.Append("> {");
            sb.Append(arena?.Name ?? "(no arena)");
            sb.Append("} ");
            sb.Append(message);

            try
            {
                _logQueue.Add(
                    new LogEntry()
                    {
                        Level = level,
                        Module = module,
                        Arena = arena,
                        LogText = sb,
                    });
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogP(LogLevel level, string module, Player player, ReadOnlySpan<char> message)
        {
            Arena arena = player?.Arena;

            StringBuilder sb = _stringBuilderPool.Get();
            sb.Append(level.ToChar());
            sb.Append(" <");
            sb.Append(module);
            sb.Append("> {");
            sb.Append(arena?.Name ?? "(no arena)");
            sb.Append("} [");
            sb.Append(player?.Name ?? ((player != null) ? "pid=" + player.Id : null) ?? "(null player)");
            sb.Append("] ");
            sb.Append(message);

            try
            {
                _logQueue.Add(
                    new LogEntry()
                    {
                        Level = level,
                        Module = module,
                        Arena = arena,
                        Player = player,
                        LogText = sb,
                    });
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogP(LogLevel level, string module, Player player, StringBuilder message)
        {
            Arena arena = player?.Arena;

            StringBuilder sb = _stringBuilderPool.Get();
            sb.Append(level.ToChar());
            sb.Append(" <");
            sb.Append(module);
            sb.Append("> {");
            sb.Append(arena?.Name ?? "(no arena)");
            sb.Append("} [");
            sb.Append(player?.Name ?? ((player != null) ? "pid=" + player.Id : null) ?? "(null player)");
            sb.Append("] ");
            sb.Append(message);

            try
            {
                _logQueue.Add(
                    new LogEntry()
                    {
                        Level = level,
                        Module = module,
                        Arena = arena,
                        Player = player,
                        LogText = sb,
                    });
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        bool ILogManager.FilterLog(in LogEntry logEntry, string logModuleName)
        {
            if (string.IsNullOrWhiteSpace(logModuleName))
                return true;

            _rwLock.EnterReadLock();

            try
            {
                if (_configManager == null)
                    return true; // filtering disabled

                string origin = logEntry.Module;
                if (string.IsNullOrWhiteSpace(origin))
                    origin = "unknown";

                string settingValue = _configManager.GetStr(_configManager.Global, logModuleName, origin);
                if (settingValue == null)
                {
                    settingValue = _configManager.GetStr(_configManager.Global, logModuleName, "all");
                    if (settingValue == null)
                        return true; // filtering disabled
                }

                if (!settingValue.Contains(logEntry.Level.ToChar()))
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

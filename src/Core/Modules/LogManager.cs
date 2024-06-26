using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
	/// <summary>
	/// Module that provides logging functionality.
	/// </summary>
	[CoreModuleInfo]
    public sealed class LogManager : IModule, IModuleLoaderAware, ILogManager, IStringBuilderPoolProvider, IDisposable
    {
        private ComponentBroker _broker;
        private IObjectPoolManager _objectPoolManager;
        private InterfaceRegistrationToken<ILogManager> _iLogManagerToken;

        private DefaultObjectPool<StringBuilder> _stringBuilderPool;
        private readonly BlockingCollection<LogEntry> _logQueue = new(4096);
        private Thread _loggingThread;

        private readonly ReaderWriterLockSlim _rwLock = new();
        private IConfigManager _configManager = null;

        #region Module Members

        public bool Load(ComponentBroker broker, IObjectPoolManager objectPoolManager)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

			// IObjectPoolManager provides a pool of StringBuilder objects, but those are meant for
            // scenarios where a single thread synchronously rents, uses, and then returns the object.
            // We need StringBuilder objects that will be passed through a producer-consumer queue,
            // such that the objects are asynchronously processed. We have one or more threads producing,
            // and a dedicated thread that consumes. Therefore, this uses its own separate pool.
			_stringBuilderPool = new DefaultObjectPool<StringBuilder>(
                new StringBuilderPooledObjectPolicy()
                {
                    InitialCapacity = 1024,
                    MaximumRetainedCapacity = 4096,
                },
				32768 // an arbitrarily large retention limit that it ordinarily should never get to
			);

            _iLogManagerToken = broker.RegisterInterface<ILogManager>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iLogManagerToken) != 0)
                return false;

            _logQueue.CompleteAdding();

            Thread workerThread;

            _rwLock.EnterReadLock();

            try
            {
                workerThread = _loggingThread;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            workerThread?.Join();

            _rwLock.EnterWriteLock();

            try
            {
                _loggingThread = null;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

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

                _loggingThread = new Thread(new ThreadStart(LoggingThread))
                {
                    Name = nameof(LogManager)
                };
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            _loggingThread.Start();

            return true;
        }

        bool IModuleLoaderAware.PreUnload(ComponentBroker broker)
        {
            _rwLock.EnterWriteLock();

            try
            {
                if (_configManager is not null)
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

        void ILogManager.Log(LogLevel level, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level);
			sb.Append(' ');
            handler.CopyToAndClear(sb);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.Log(LogLevel level, IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((ILogManager)this).Log(level, ref handler);
        }

        void ILogManager.Log(LogLevel level, ReadOnlySpan<char> message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level);
			sb.Append(' ');
            sb.Append(message);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.Log(LogLevel level, string message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level);
			sb.Append(' ');
            sb.Append(message);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.Log(LogLevel level, StringBuilder message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            Append(sb, level);
            sb.Append(' ');
            sb.Append(message);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogM(LogLevel level, string module, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level, module);
			sb.Append(' ');
			handler.CopyToAndClear(sb);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    Module = module,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogM(LogLevel level, string module, IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((ILogManager)this).LogM(level, module, ref handler);
        }

        void ILogManager.LogM(LogLevel level, string module, ReadOnlySpan<char> message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level, module);
			sb.Append(' ');
			sb.Append(message);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    Module = module,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogM(LogLevel level, string module, string message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level, module);
			sb.Append(' ');
			sb.Append(message);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    Module = module,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogM(LogLevel level, string module, StringBuilder message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level, module);
			sb.Append(' ');
			sb.Append(message);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    Module = module,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogA(LogLevel level, string module, Arena arena, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level, module, arena);
			sb.Append(' ');
			handler.CopyToAndClear(sb);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    Module = module,
                    Arena = arena,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogA(LogLevel level, string module, Arena arena, IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((ILogManager)this).LogA(level, module, arena, ref handler);
        }

        void ILogManager.LogA(LogLevel level, string module, Arena arena, ReadOnlySpan<char> message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level, module, arena);
			sb.Append(' ');
			sb.Append(message);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    Module = module,
                    Arena = arena,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogA(LogLevel level, string module, Arena arena, string message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level, module, arena);
			sb.Append(' ');
			sb.Append(message);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    Module = module,
                    Arena = arena,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogA(LogLevel level, string module, Arena arena, StringBuilder message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            Append(sb, level, module, arena);
            sb.Append(' ');
            sb.Append(message);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    Module = module,
                    Arena = arena,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogP(LogLevel level, string module, Player player, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            Append(sb, level, module, player);
			sb.Append(' ');
			handler.CopyToAndClear(sb);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    Module = module,
                    Arena = player?.Arena,
                    PlayerName = player?.Name,
                    PlayerId = player?.Id,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogP(LogLevel level, string module, Player player, IFormatProvider provider, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            ((ILogManager)this).LogP(level, module, player, ref handler);
        }

        void ILogManager.LogP(LogLevel level, string module, Player player, ReadOnlySpan<char> message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level, module, player);
			sb.Append(' ');
			sb.Append(message);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    Module = module,
                    Arena = player?.Arena,
                    PlayerName = player?.Name,
                    PlayerId = player?.Id,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogP(LogLevel level, string module, Player player, string message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level, module, player);
			sb.Append(' ');
			sb.Append(message);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    Module = module,
                    Arena = player?.Arena,
                    PlayerName = player?.Name,
                    PlayerId = player?.Id,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
            }
            catch (InvalidOperationException)
            {
                _stringBuilderPool.Return(sb);
            }
        }

        void ILogManager.LogP(LogLevel level, string module, Player player, StringBuilder message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
			Append(sb, level, module, player);
			sb.Append(' ');
			sb.Append(message);

            try
            {
                LogEntry logEntry = new()
                {
                    Level = level,
                    Module = module,
                    Arena = player?.Arena,
                    PlayerName = player?.Name,
                    PlayerId = player?.Id,
                    LogText = sb,
                };

                QueueOrWriteLog(ref logEntry);
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
                if (_configManager is null)
                    return true; // filtering disabled

                string origin = logEntry.Module;
                if (string.IsNullOrWhiteSpace(origin))
                    origin = "unknown";

                string settingValue = _configManager.GetStr(_configManager.Global, logModuleName, origin);
                if (settingValue is null)
                {
                    settingValue = _configManager.GetStr(_configManager.Global, logModuleName, "all");
                    if (settingValue is null)
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

        ObjectPool<StringBuilder> IStringBuilderPoolProvider.StringBuilderPool => _objectPoolManager.StringBuilderPool;

		#endregion

		private static void Append(StringBuilder sb, LogLevel level)
        {
            ArgumentNullException.ThrowIfNull(sb);

            sb.Append(level.ToChar());
        }

        private static void Append(StringBuilder sb, LogLevel level, string module)
        {
			ArgumentNullException.ThrowIfNull(sb);

			Append(sb, level);
			sb.Append(' ');
			sb.Append($"<{module}>");
		}

        private static void Append(StringBuilder sb, LogLevel level, string module, Arena arena)
        {
			ArgumentNullException.ThrowIfNull(sb);

			Append(sb, level, module);
			sb.Append(" {");
			sb.Append(arena?.Name ?? "(no arena)");
            sb.Append('}');
		}

        private static void Append(StringBuilder sb, LogLevel level, string module, Player player)
        {
			ArgumentNullException.ThrowIfNull(sb);

			Append(sb, level, module, player.Arena);
            sb.Append(" [");

			if (player is not null)
			{
				if (player.Name is not null)
					sb.Append(player.Name);
				else
					sb.Append($"pid={player.Id}");
			}
			else
			{
				sb.Append("(null player)");
			}

            sb.Append(']');
		}

		private void QueueOrWriteLog(ref LogEntry logEntry)
        {
            bool doAsync;

            _rwLock.EnterReadLock();

            try
            {
                doAsync = _loggingThread is not null;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            if (doAsync && !_logQueue.IsAddingCompleted)
            {
                _logQueue.Add(logEntry);
            }
            else
            {
                WriteLog(ref logEntry);
            }
        }

        private void WriteLog(ref LogEntry logEntry)
        {
            try
            {
                LogCallback.Fire(_broker, logEntry);
            }
            catch (Exception ex)
            {
                StringBuilder sb = _stringBuilderPool.Get();

                try
                {
                    sb.Append($"E <{nameof(LogManager)}> An exception was thrown when firing the Log callback. " +
                        $"This indicates a problem in one of the logging modules that needs to be investigated. {ex}");

                    Console.Error.WriteLine(sb);
                }
                finally
                {
                    _stringBuilderPool.Return(sb);
                }
            }
            finally
            {
                _stringBuilderPool.Return(logEntry.LogText);
            }
        }

        private void LoggingThread()
        {
            try
            {
                while (!_logQueue.IsCompleted)
                {
                    if (!_logQueue.TryTake(out LogEntry logEntry, Timeout.Infinite))
                        continue;

                    WriteLog(ref logEntry);
                }
            }
            catch (Exception ex)
            {
                StringBuilder sb = _stringBuilderPool.Get();
                sb.Append($"{LogLevel.Error.ToChar()} <{nameof(LogManager)}> LoggingThread ending due to an unexpected exception. {ex}");

                LogEntry logEntry = new()
                {
                    Level = LogLevel.Error,
                    Module = nameof(LogManager),
                    LogText = sb,
                };

                WriteLog(ref logEntry);
            }
        }

        public void Dispose()
        {
            _logQueue.Dispose();
        }
    }
}

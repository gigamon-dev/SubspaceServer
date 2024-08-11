using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides logging functionality.
    /// </summary>
    [CoreModuleInfo]
    public sealed class LogManager : IModule, IModuleLoaderAware, ILogManager, IStringBuilderPoolProvider, IDisposable
    {
        /// <summary>
        /// A reasonable limit, way higher than we should ever reach, but in the worst case scenario, not too high memory-wise.
        /// </summary>
        private const int ChannelCapacity = 8192;

        private readonly IComponentBroker _broker;
        private readonly IObjectPoolManager _objectPoolManager;
        private InterfaceRegistrationToken<ILogManager>? _iLogManagerToken;

        // Optional dependency that gets loaded after this module.
        private IConfigManager? _configManager;

        private readonly ReaderWriterLockSlim _rwLock = new();

        // IObjectPoolManager provides a pool of StringBuilder objects, but those are meant for
        // scenarios where a thread synchronously rents, uses, and then returns the object.
        // We need StringBuilder objects that will be passed through a producer-consumer queue,
        // such that the objects are asynchronously processed. Therefore, this uses its own separate pool.
        private readonly DefaultObjectPool<StringBuilder> _stringBuilderPool = new(
                new StringBuilderPooledObjectPolicy()
                {
                    InitialCapacity = 1024,
                    MaximumRetainedCapacity = 4096,
                },
                ChannelCapacity + Environment.ProcessorCount
            );

        private readonly Channel<LogEntry> _logChannel;
        private Task? _loggingTask;
        private int _dropCount = 0;

        public LogManager(
            IComponentBroker broker,
            IObjectPoolManager objectPoolManager)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            _logChannel = Channel.CreateBounded<LogEntry>(
                new BoundedChannelOptions(ChannelCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.DropWrite,
                },
                ProcessDropped);
        }

        #region Module Members

        bool IModule.Load(IComponentBroker broker)
        {
            _iLogManagerToken = broker.RegisterInterface<ILogManager>(this);
            return true;
        }

        void IModuleLoaderAware.PostLoad(IComponentBroker broker)
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

            _loggingTask = Task.Run(ProcessLogs);
        }

        void IModuleLoaderAware.PreUnload(IComponentBroker broker)
        {
            _rwLock.EnterWriteLock();

            try
            {
                if (_configManager is not null)
                    broker.ReleaseInterface(ref _configManager);
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iLogManagerToken) != 0)
                return false;

            _logChannel.Writer.Complete();
            _loggingTask?.Wait();
            return true;
        }

        #endregion

        #region ILogManager Members

        #region Log

        void ILogManager.Log(LogLevel level, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            Append(sb, level);
            sb.Append(' ');
            handler.CopyToAndClear(sb);

            LogEntry logEntry = new()
            {
                Level = level,
                LogText = sb,
            };

            QueueOrWriteLog(ref logEntry);
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

            LogEntry logEntry = new()
            {
                Level = level,
                LogText = sb,
            };

            QueueOrWriteLog(ref logEntry);
        }

        void ILogManager.Log(LogLevel level, string message)
        {
            ((ILogManager)this).Log(level, message.AsSpan());
        }

        void ILogManager.Log(LogLevel level, StringBuilder message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            Append(sb, level);
            sb.Append(' ');
            sb.Append(message);

            LogEntry logEntry = new()
            {
                Level = level,
                LogText = sb,
            };

            QueueOrWriteLog(ref logEntry);
        }

        #endregion

        #region Log Module

        void ILogManager.LogM(LogLevel level, string module, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            Append(sb, level, module);
            sb.Append(' ');
            handler.CopyToAndClear(sb);

            LogEntry logEntry = new()
            {
                Level = level,
                Module = module,
                LogText = sb,
            };

            QueueOrWriteLog(ref logEntry);
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

            LogEntry logEntry = new()
            {
                Level = level,
                Module = module,
                LogText = sb,
            };

            QueueOrWriteLog(ref logEntry);
        }

        void ILogManager.LogM(LogLevel level, string module, string message)
        {
            ((ILogManager)this).LogM(level, module, message.AsSpan());
        }

        void ILogManager.LogM(LogLevel level, string module, StringBuilder message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            Append(sb, level, module);
            sb.Append(' ');
            sb.Append(message);

            LogEntry logEntry = new()
            {
                Level = level,
                Module = module,
                LogText = sb,
            };

            QueueOrWriteLog(ref logEntry);
        }

        #endregion

        #region Log Arena

        void ILogManager.LogA(LogLevel level, string module, Arena arena, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            Append(sb, level, module, arena);
            sb.Append(' ');
            handler.CopyToAndClear(sb);

            LogEntry logEntry = new()
            {
                Level = level,
                Module = module,
                Arena = arena,
                LogText = sb,
            };

            QueueOrWriteLog(ref logEntry);
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

            LogEntry logEntry = new()
            {
                Level = level,
                Module = module,
                Arena = arena,
                LogText = sb,
            };

            QueueOrWriteLog(ref logEntry);
        }

        void ILogManager.LogA(LogLevel level, string module, Arena arena, string message)
        {
            ((ILogManager)this).LogA(level, module, arena, message.AsSpan());
        }

        void ILogManager.LogA(LogLevel level, string module, Arena arena, StringBuilder message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            Append(sb, level, module, arena);
            sb.Append(' ');
            sb.Append(message);

            LogEntry logEntry = new()
            {
                Level = level,
                Module = module,
                Arena = arena,
                LogText = sb,
            };

            QueueOrWriteLog(ref logEntry);
        }

        #endregion

        #region Log Player

        void ILogManager.LogP(LogLevel level, string module, Player player, ref StringBuilderBackedInterpolatedStringHandler handler)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            Append(sb, level, module, player);
            sb.Append(' ');
            handler.CopyToAndClear(sb);

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

        void ILogManager.LogP(LogLevel level, string module, Player player, string message)
        {
            ((ILogManager)this).LogP(level, module, player, message.AsSpan());
        }

        void ILogManager.LogP(LogLevel level, string module, Player player, StringBuilder message)
        {
            StringBuilder sb = _stringBuilderPool.Get();
            Append(sb, level, module, player);
            sb.Append(' ');
            sb.Append(message);

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

        #endregion

        bool ILogManager.FilterLog(ref readonly LogEntry logEntry, string logModuleName)
        {
            if (string.IsNullOrWhiteSpace(logModuleName))
                return true;

            _rwLock.EnterReadLock();

            try
            {
                if (_configManager is null)
                    return true; // filtering disabled

                string? origin = logEntry.Module;
                if (string.IsNullOrWhiteSpace(origin))
                    origin = "unknown";

                string? settingValue = _configManager.GetStr(_configManager.Global, logModuleName, origin);
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

        #endregion

        #region IStringBuilderPoolProvider

        ObjectPool<StringBuilder> IStringBuilderPoolProvider.StringBuilderPool => _objectPoolManager.StringBuilderPool;

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _rwLock.Dispose();
        }

        #endregion

        #region Append

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

        private static void Append(StringBuilder sb, LogLevel level, string module, Arena? arena)
        {
            ArgumentNullException.ThrowIfNull(sb);

            Append(sb, level, module);
            sb.Append(" {");
            sb.Append(arena?.Name ?? "(no arena)");
            sb.Append('}');
        }

        private static void Append(StringBuilder sb, LogLevel level, string module, Player? player)
        {
            ArgumentNullException.ThrowIfNull(sb);

            Append(sb, level, module, player?.Arena);
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

        #endregion

        private void QueueOrWriteLog(ref LogEntry logEntry)
        {
            if (!_logChannel.Writer.TryWrite(logEntry))
            {
                WriteLog(ref logEntry);
            }
        }

        private void WriteLog(ref readonly LogEntry logEntry)
        {
            try
            {
                LogCallback.Fire(_broker, in logEntry);
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

        private async ValueTask ProcessLogs()
        {
            ChannelReader<LogEntry> reader = _logChannel.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out LogEntry logEntry))
                {
                    WriteLog(ref logEntry);
                }
            }
        }

        private void ProcessDropped(LogEntry logEntry)
        {
            _stringBuilderPool.Return(logEntry.LogText);

            LogDroppedCallback.Fire(_broker, Interlocked.Increment(ref _dropCount));
        }
    }
}

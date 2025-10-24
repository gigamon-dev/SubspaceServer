using SS.Core.ComponentAdvisors;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that advises on whether accessing a config setting requires extra authorization, 
    /// the <see cref="Constants.Capabilities.AllowRestrictedSettings"/> capability.
    /// <para>
    /// Configure which global.conf settings require additional authorization in: '/conf/cfgauthg.conf'
    /// </para>
    /// <para>
    /// Configure which arena.conf settings require additional authorization in: '/conf/cfgautha.conf'
    /// </para>
    /// </summary>
    public sealed class ConfigAuthorize(
        ILogManager logManager,
        IObjectPoolManager objectPoolManager) : IAsyncModule, IConfigManagerAdvisor, IDisposable
    {
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private AdvisorRegistrationToken<IConfigManagerAdvisor>? _iConfigManagerAdvisorToken;

        private const string ConfPrefix = "cfgauth";
        private const string GlobalConfFileName = $"{ConfPrefix}g.conf";
        private const string ArenaConfFileName = $"{ConfPrefix}a.conf";
        private const string GlobalConfigPath = $"conf/{GlobalConfFileName}";
        private const string ArenaConfigPath = $"conf/{ArenaConfFileName}";

        private FileSystemWatcher? _fileSystemWatcher;

        private readonly Lock _loadLock = new();
        private bool _isLoadGlobalRequested = false;
        private bool _isLoadArenaRequested = false;
        private Task? _loadTask = null;

        private readonly Lock _dataLock = new();
        private uint? _globalChecksum = null;
        private uint? _arenaChecksum = null;

        /// <summary>
        /// global.conf settings that are restricted.
        /// </summary>
        /// <remarks>
        /// Synchronized with <see cref="_dataLock"/>.
        /// </remarks>
        private HashSet<string> _globalRestrictedSet = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// arena.conf settings that are restricted.
        /// </summary>
        /// <remarks>
        /// Synchronized with <see cref="_dataLock"/>.
        /// </remarks>
        private HashSet<string> _arenaRestrictedSet = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The load task modifies this set, then swaps it with the actual (<see cref="_arenaRestrictedSet"/> or <see cref="_globalRestrictedSet"/>).
        /// </summary>
        private HashSet<string> _tempRestrictedSet = new(StringComparer.OrdinalIgnoreCase);

        #region Module members

        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _fileSystemWatcher = new("./conf", $"{ConfPrefix}?.conf");
            _fileSystemWatcher.NotifyFilter = NotifyFilters.LastWrite;
            _fileSystemWatcher.Changed += FileSystemWatcher_Changed;
            _fileSystemWatcher.EnableRaisingEvents = true;

            await QueueLoad(true, true);
            _iConfigManagerAdvisorToken = broker.RegisterAdvisor<IConfigManagerAdvisor>(this);
            return true;
        }

        Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (!broker.UnregisterAdvisor(ref _iConfigManagerAdvisorToken))
                return Task.FromResult(false);

            lock (_dataLock)
            {
                _globalChecksum = null;
                _globalRestrictedSet.Clear();

                _arenaChecksum = null;
                _arenaRestrictedSet.Clear();
            }

            Dispose();
            return Task.FromResult(true);
        }

        #endregion

        #region IConfigManagerAdvisor

        bool IConfigManagerAdvisor.IsArenaConfRestrictedSetting(ReadOnlySpan<char> section, ReadOnlySpan<char> key)
        {
            lock (_dataLock)
            {
                return IsRestricted(_arenaRestrictedSet, section, key);
            }
        }

        bool IConfigManagerAdvisor.IsGlobalConfRestrictedSetting(ReadOnlySpan<char> section, ReadOnlySpan<char> key)
        {
            lock (_dataLock)
            {
                return IsRestricted(_globalRestrictedSet, section, key);
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_fileSystemWatcher is not null)
            {
                _fileSystemWatcher.EnableRaisingEvents = false;
                _fileSystemWatcher.Changed -= FileSystemWatcher_Changed;
                _fileSystemWatcher.Dispose();
                _fileSystemWatcher = null;
            }
        }

        #endregion

        private static bool IsRestricted(HashSet<string> restrictedSet, ReadOnlySpan<char> section, ReadOnlySpan<char> key)
        {
            if (!restrictedSet.TryGetAlternateLookup(out HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup))
                return false;

            if (lookup.Contains(section))
                return true;

            int length = section.Length + 1 + key.Length;
            char[]? bufferArray = null;
            try
            {
                Span<char> bufferSpan = length > 1024
                ? (bufferArray = ArrayPool<char>.Shared.Rent(length)).AsSpan(0, length)
                : stackalloc char[length];

                if (!bufferSpan.TryWrite($"{section}:{key}", out int charsWritten) || charsWritten != length)
                    return false;

                return lookup.Contains(bufferSpan);
            }
            finally
            {
                if (bufferArray is not null)
                    ArrayPool<char>.Shared.Return(bufferArray);
            }
        }

        private void FileSystemWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            if (string.Equals(e.Name, GlobalConfFileName, StringComparison.OrdinalIgnoreCase))
            {
                QueueLoad(true, false);
            }
            else if (string.Equals(e.Name, ArenaConfFileName, StringComparison.OrdinalIgnoreCase))
            {
                QueueLoad(false, true);
            }
        }

        private Task QueueLoad(bool loadGlobal, bool loadArena)
        {
            if (!loadGlobal && !loadArena)
                return Task.CompletedTask;

            lock (_loadLock)
            {
                if (loadGlobal)
                    _isLoadGlobalRequested = true;

                if (loadArena)
                    _isLoadArenaRequested = true;

                return _loadTask ??= Task.Run(LoadAsync);
            }
        }

        private async Task LoadAsync()
        {
            while (true)
            {
                bool loadGlobal;
                bool loadArena;

                lock (_loadLock)
                {
                    loadGlobal = _isLoadGlobalRequested;
                    loadArena = _isLoadArenaRequested;

                    if (!loadGlobal && !loadArena)
                    {
                        _loadTask = null;
                        break;
                    }

                    _isLoadGlobalRequested = false;
                    _isLoadArenaRequested = false;
                }

                if (loadGlobal)
                {
                    await ProcessAsync(true).ConfigureAwait(false);
                }

                if (loadArena)
                {
                    await ProcessAsync(false).ConfigureAwait(false);
                }
            }

            async Task ProcessAsync(bool isGlobal)
            {
                string configPath = isGlobal ? GlobalConfigPath : ArenaConfigPath;
                uint checksum;

                try
                {
                    //
                    // Open the file.
                    //

                    FileStream? fileStream = null;
                    int tries = 0;

                    do
                    {
                        try
                        {
                            fileStream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                        }
                        catch (IOException ex) when (ex is not FileNotFoundException && ex is not DirectoryNotFoundException)
                        {
                            // Note: This retry logic is to workaround the "The process cannot access the file because it is being used by another process." race condition.
                            if (++tries >= 10)
                            {
                                _logManager.LogM(LogLevel.Error, nameof(ConfigAuthorize), $"Error opening {configPath} ({tries} tries). {ex.Message}");
                                return;
                            }

                            _logManager.LogM(LogLevel.Drivel, nameof(ConfigAuthorize), $"Error opening {configPath} ({tries} tries). {ex.Message}");

                            await Task.Delay(1000).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(ConfigAuthorize), $"Error opening {configPath}. {ex.Message}");
                            return;
                        }
                    }
                    while (fileStream is null);

                    try
                    {
                        //
                        // Checksum
                        //

                        Crc32 crc32 = _objectPoolManager.Crc32Pool.Get();

                        try
                        {
                            await crc32.AppendAsync(fileStream).ConfigureAwait(false);
                            checksum = crc32.GetCurrentHashAsUInt32();
                        }
                        finally
                        {
                            _objectPoolManager.Crc32Pool.Return(crc32);
                        }

                        bool isUnchanged;

                        lock (_dataLock)
                        {
                            if (isGlobal)
                                isUnchanged = _globalChecksum is not null && _globalChecksum == checksum;
                            else
                                isUnchanged = _arenaChecksum is not null && _arenaChecksum == checksum;
                        }

                        if (isUnchanged)
                        {
                            _logManager.LogM(LogLevel.Drivel, nameof(ConfigAuthorize), $"Checked {configPath}, but there was no change (checksum {checksum:X})");
                            return; // no change
                        }

                        fileStream.Position = 0;

                        //
                        // Read the data.
                        //

                        _tempRestrictedSet.Clear();

                        string? line;
                        using StreamReader sr = new(fileStream, StringUtils.DefaultEncoding, true, -1, true);
                        while ((line = await sr.ReadLineAsync(CancellationToken.None)) != null)
                        {
                            if (line.Length > 0 && !line.StartsWith('#'))
                            {
                                _tempRestrictedSet.Add(line);
                            }
                        }
                    }
                    finally
                    {
                        await fileStream.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(ConfigAuthorize), $"Error loading {configPath}. {ex.Message}");
                    return;
                }

                int count = _tempRestrictedSet.Count;

                lock (_dataLock)
                {
                    if (isGlobal)
                    {
                        _globalChecksum = checksum;

                        // Swap
                        (_tempRestrictedSet, _globalRestrictedSet) = (_globalRestrictedSet, _tempRestrictedSet);
                    }
                    else
                    {
                        _arenaChecksum = checksum;

                        // Swap
                        (_tempRestrictedSet, _arenaRestrictedSet) = (_arenaRestrictedSet, _tempRestrictedSet);
                    }
                }

                _tempRestrictedSet.Clear();

                _logManager.LogM(LogLevel.Info, nameof(ConfigAuthorize), $"Loaded {configPath} (count:{count}, checksum:{checksum:X}).");
            }
        }
    }
}

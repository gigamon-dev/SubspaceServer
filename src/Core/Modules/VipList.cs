using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that decides which players are VIPs (vip.txt).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A VIP is trusted to run a non-standard client: a Continuum pre-release, a modified client or a bot.
    /// A player is a VIP if any of these is true:
    /// <list type="bullet">
    /// <item>Its name is listed in vip.txt (case-insensitive), or vip.txt has a line that is just <c>*!</c>, which lists everyone.</item>
    /// <item>Vip:AllVips is set.</item>
    /// <item>Vip:AllowPrerelease is set and it runs a Continuum newer than the one the server asks for.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Lines starting with # are comments. Any other line is a name only if it starts with a letter or a digit
    /// and is shorter than 32 characters. Trailing white-space is trimmed. The file is reloaded when it changes.
    /// </para>
    /// <para>
    /// Checking a name does not allocate. A reload is skipped when the file's checksum is unchanged; otherwise the names are read into
    /// a second set that is swapped in and reused on the next reload, keeping the strings of names that were already listed.
    /// </para>
    /// <para>
    /// What being a VIP does is up to the modules that check <see cref="Player.PlayerFlags.IsVip"/>:
    /// the login response tells the client to skip its exe and code checksum checks (unless the player gets the moderator checksums),
    /// a Continuum pre-release VIP is not asked to change version, the security check does not kick for an exe checksum mismatch,
    /// and a client-reported data checksum mismatch is ignored.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public sealed class VipList(
        IConfigManager configManager,
        ILogManager logManager,
        IObjectPoolManager objectPoolManager) : IAsyncModule, IVipList, IDisposable
    {
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

        private InterfaceRegistrationToken<IVipList>? _iVipListRegistrationToken;

        private const string VipFileName = "vip.txt";

        /// <summary>
        /// A vip.txt line that lists every player.
        /// </summary>
        private const string Wildcard = "*!";

        /// <summary>
        /// A vip.txt line of this many characters or more is ignored.
        /// </summary>
        private const int MaxLineLength = 32;

        private FileSystemWatcher? _fileSystemWatcher;

        private readonly Lock _loadLock = new();
        private bool _isLoadRequested = false;
        private Task? _loadTask = null;

        private readonly Lock _vipLock = new();

        /// <summary>
        /// The names in use. Only the loader replaces it (under <see cref="_vipLock"/>); readers only read it.
        /// </summary>
        private HashSet<string> _vipNames = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _vipNamesLookup;

        /// <summary>
        /// The set the next reload fills, then swaps with <see cref="_vipNames"/>. Used only by the loader.
        /// </summary>
        private HashSet<string> _spareVipNames = new(StringComparer.OrdinalIgnoreCase);

        private bool _isWildcard = false;

        /// <summary>
        /// The checksum of the vip.txt last loaded, or null if there was none. Used only by the loader.
        /// </summary>
        private uint? _checksum = null;

        /// <summary>
        /// Whether a load has completed. Used only by the loader.
        /// </summary>
        private bool _hasLoaded = false;

        private bool _allVips;
        private bool _allowPrerelease;

        #region Module members

        [ConfigHelp<bool>("Vip", "AllVips", ConfigScope.Global, Default = false,
            Description = "Whether every player is a VIP.")]
        [ConfigHelp<bool>("Vip", "AllowPrerelease", ConfigScope.Global, Default = false,
            Description = "Whether a player running a Continuum newer than the one the server asks for is a VIP. Such a player is not asked to change version.")]
        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _allVips = _configManager.GetBool(_configManager.Global, "Vip", "AllVips", ConfigHelp.Constants.Global.Vip.AllVips.Default);
            _allowPrerelease = _configManager.GetBool(_configManager.Global, "Vip", "AllowPrerelease", ConfigHelp.Constants.Global.Vip.AllowPrerelease.Default);

            _vipNamesLookup = _vipNames.GetAlternateLookup<ReadOnlySpan<char>>();

            _fileSystemWatcher = new(".", VipFileName);
            _fileSystemWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
            _fileSystemWatcher.Changed += FileSystemWatcher_Changed;
            _fileSystemWatcher.Created += FileSystemWatcher_Changed;
            _fileSystemWatcher.Deleted += FileSystemWatcher_Changed;
            _fileSystemWatcher.Renamed += FileSystemWatcher_Changed;
            _fileSystemWatcher.EnableRaisingEvents = true;

            await QueueLoadVipList().ConfigureAwait(false);

            _iVipListRegistrationToken = broker.RegisterInterface<IVipList>(this);
            return true;
        }

        Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (broker.UnregisterInterface(ref _iVipListRegistrationToken) != 0)
                return Task.FromResult(false);

            Dispose();

            lock (_vipLock)
            {
                _vipNames.Clear();
                _isWildcard = false;
            }

            return Task.FromResult(true);
        }

        #endregion

        #region IVipList

        bool IVipList.IsClientVip(ClientType clientType, ushort clientVersion, ushort serverContinuumVersion)
        {
            return _allVips
                || (_allowPrerelease && clientType == ClientType.Continuum && clientVersion > serverContinuumVersion);
        }

        bool IVipList.IsListed(ReadOnlySpan<char> name)
        {
            lock (_vipLock)
            {
                return _isWildcard || _vipNamesLookup.Contains(name.TrimEnd());
            }
        }

        #endregion

        private void FileSystemWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            QueueLoadVipList();
        }

        private Task QueueLoadVipList()
        {
            lock (_loadLock)
            {
                _isLoadRequested = true;
                return _loadTask ??= Task.Run(LoadVipListAsync);
            }
        }

        private async Task LoadVipListAsync()
        {
            while (true)
            {
                lock (_loadLock)
                {
                    if (!_isLoadRequested)
                    {
                        _loadTask = null;
                        break;
                    }

                    _isLoadRequested = false;
                }

                await ProcessVipListAsync().ConfigureAwait(false);
            }

            async Task ProcessVipListAsync()
            {
                // Fill the spare set, keeping the string of any name that is already listed. Only this loader changes the sets,
                // and readers do not change them, so reading _vipNamesLookup here without the lock is safe.
                HashSet<string> vipNames = _spareVipNames;
                vipNames.Clear();
                HashSet<string>.AlternateLookup<ReadOnlySpan<char>> currentNames = _vipNamesLookup;
                bool isWildcard = false;
                uint? checksum = null;

                if (!File.Exists(VipFileName))
                {
                    if (_hasLoaded && _checksum is null)
                        return; // still no file
                }
                else
                {
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
                                fileStream = new FileStream(VipFileName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                            }
                            catch (IOException ex) when (ex is not FileNotFoundException && ex is not DirectoryNotFoundException)
                            {
                                // Note: This retry logic is to workaround the "The process cannot access the file because it is being used by another process." race condition.
                                if (++tries >= 10)
                                {
                                    _logManager.LogM(LogLevel.Error, nameof(VipList), $"Error opening {VipFileName} ({tries} tries). {ex.Message}");
                                    return;
                                }

                                _logManager.LogM(LogLevel.Drivel, nameof(VipList), $"Error opening {VipFileName} ({tries} tries). {ex.Message}");

                                await Task.Delay(1000).ConfigureAwait(false);
                            }
                        }
                        while (fileStream is null);

                        try
                        {
                            //
                            // Checksum: skip the reload if the file is unchanged (a save often raises several change events).
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

                            if (_hasLoaded && _checksum == checksum)
                            {
                                _logManager.LogM(LogLevel.Drivel, nameof(VipList), $"Checked {VipFileName}, but there was no change (checksum {checksum:X}).");
                                return;
                            }

                            fileStream.Position = 0;

                            //
                            // Read the names.
                            //

                            using StreamReader sr = new(fileStream, StringUtils.DefaultEncoding, true, -1, true);

                            string? line;
                            while ((line = await sr.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) != null)
                            {
                                // Lines starting with # are comments.
                                if (line.Length == 0 || line.StartsWith('#') || line.Length >= MaxLineLength)
                                    continue;

                                ReadOnlySpan<char> name = line.AsSpan().TrimEnd();
                                if (name.IsEmpty)
                                    continue;

                                if (name.SequenceEqual(Wildcard))
                                {
                                    isWildcard = true;
                                }
                                else if (char.IsAsciiLetterOrDigit(name[0]))
                                {
                                    vipNames.Add(currentNames.TryGetValue(name, out string? existing) ? existing : name.ToString());
                                }
                            }
                        }
                        finally
                        {
                            await fileStream.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                    {
                        // Deleted while loading: treat it as empty.
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(VipList), $"Error loading {VipFileName}. {ex.Message}");
                        return;
                    }
                }

                // Swap the sets. The old one becomes the spare for the next reload.
                lock (_vipLock)
                {
                    _spareVipNames = _vipNames;
                    _vipNames = vipNames;
                    _vipNamesLookup = vipNames.GetAlternateLookup<ReadOnlySpan<char>>();
                    _isWildcard = isWildcard;
                }

                _checksum = checksum;
                _hasLoaded = true;

                _logManager.LogM(LogLevel.Info, nameof(VipList), $"Loaded {VipFileName} (names:{vipNames.Count}{(isWildcard ? ", everyone (*!)" : "")}).");
            }
        }

        public void Dispose()
        {
            if (_fileSystemWatcher is not null)
            {
                _fileSystemWatcher.EnableRaisingEvents = false;
                _fileSystemWatcher.Changed -= FileSystemWatcher_Changed;
                _fileSystemWatcher.Created -= FileSystemWatcher_Changed;
                _fileSystemWatcher.Deleted -= FileSystemWatcher_Changed;
                _fileSystemWatcher.Renamed -= FileSystemWatcher_Changed;
                _fileSystemWatcher.Dispose();
                _fileSystemWatcher = null;
            }
        }
    }
}

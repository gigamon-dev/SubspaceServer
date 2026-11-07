using Microsoft.Extensions.ObjectPool;
using Microsoft.Win32.SafeHandles;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that sends zone advertisements: banner images that the client shows on screen for a while.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Advertisements are loaded from the zone's root folder: <c>advert_0.bmp</c> to <c>advert_127.bmp</c>.
    /// The subgame names <c>b_ad0.bmp</c> to <c>b_ad127.bmp</c> and <c>r_ad0.bmp</c> to <c>r_ad127.bmp</c> are also loaded, so that a
    /// subgame zone's advertisements can be used as they are. All of them are treated alike. Each must be an uncompressed 8-bit bitmap.
    /// Its pixel values are sent as they are, as indexes into the client's own palette; the bitmap's palette is not used.
    /// </para>
    /// <para>
    /// The files are read asynchronously on a worker thread, so loading the module (including with ?insmod) does not hold up the server.
    /// They are read again whenever one of them is added, changed, renamed or deleted, and the new set replaces the old one once it is read.
    /// </para>
    /// <para>
    /// Each advertisement is sent as S2C <see cref="S2CPacketType.AdBanner"/> (0x30):
    /// <c>[0x30][u8 display mode][u16 width][u16 height][i32 duration][width * height pixels, top row first]</c>.
    /// The client shows it until the duration (in hundredths of a second) runs out. A duration of 0 or less is never shown.
    /// </para>
    /// <para>
    /// Advertise:SendMode sets when a random advertisement is sent: on entering an arena, after being killed, or both.
    /// One sent after being killed is skipped if the player was sent one in the last 30 seconds.
    /// </para>
    /// <para>
    /// The module does nothing unless Advertise:SendMode is set in global.conf (0 by default). While it is 0, it allocates nothing,
    /// loads no files and watches none. When on, each bitmap is read straight into its packet, and sending allocates nothing.
    /// Per-player data is only allocated when advertisements are sent after being killed, since only that needs the time of the last one.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public sealed class Advertisements(
        IComponentBroker broker,
        IConfigManager configManager,
        ILogManager logManager,
        INetwork network,
        IPlayerData playerData) : IAsyncModule
    {
        /// <summary>
        /// S2C 0x30 header: type, display mode, width, height, duration.
        /// </summary>
        private const int HeaderLength = 10;

        /// <summary>
        /// BITMAPFILEHEADER (14 bytes) followed by a BITMAPINFOHEADER (40 bytes).
        /// </summary>
        private const int BitmapHeaderLength = 54;

        /// <summary>
        /// The file name prefixes that are looked for: <c>advert_</c>, and the subgame names <c>b_ad</c> and <c>r_ad</c>.
        /// </summary>
        private static readonly string[] FilePrefixes = ["advert_", "b_ad", "r_ad"];

        /// <summary>
        /// The # of advertisement files of each prefix that are looked for (advert_0.bmp to advert_127.bmp, and likewise b_ad and r_ad).
        /// </summary>
        private const int MaxFilesPerPrefix = 128;

        /// <summary>
        /// After being killed, an advertisement is not sent if the player was sent one within this time.
        /// </summary>
        private const long KilledResendMilliseconds = 30000;

        /// <summary>
        /// How many times to try opening a file that is in use (most likely still being written), and how long to wait between tries.
        /// </summary>
        private const int MaxOpenTries = 10;
        private const int OpenRetryMilliseconds = 1000;

        private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        private PlayerDataKey<PlayerData> _pdKey;

        /// <summary>
        /// The loaded advertisements, each a complete S2C 0x30 packet. Empty while the module is off or none are loaded.
        /// </summary>
        /// <remarks>
        /// Only the loader replaces it, with a new array; an array is never changed once it is set here. Senders read it without a lock.
        /// </remarks>
        private volatile byte[][] _advertisements = [];

        /// <summary>
        /// Whether Advertise:SendMode was on when the module loaded.
        /// </summary>
        private bool _isEnabled;

        /// <summary>
        /// The display mode and duration put in each packet.
        /// </summary>
        private byte _displayMode;
        private int _duration;

        private FileSystemWatcher? _fileSystemWatcher;

        private readonly Lock _loadLock = new();

        /// <summary>
        /// Whether a (re)load was requested since the loader last started reading the files. Guarded by <see cref="_loadLock"/>.
        /// </summary>
        private bool _isLoadRequested;

        /// <summary>
        /// The loader, while it is running. Guarded by <see cref="_loadLock"/>.
        /// </summary>
        private Task? _loadTask;

        /// <summary>
        /// Cancels the loader when the module unloads. <see langword="null"/> while the module is off. Guarded by <see cref="_loadLock"/>.
        /// </summary>
        private CancellationTokenSource? _loadCancellation;

        /// <summary>
        /// Whether to send an advertisement when a player enters an arena.
        /// </summary>
        private bool _sendOnEnterArena;

        /// <summary>
        /// Whether to send an advertisement when a player is killed.
        /// </summary>
        private bool _sendOnKilled;

        /// <summary>
        /// Cached delegate for <see cref="GetAdvertisementData"/>, so that each send does not allocate one.
        /// </summary>
        private GetSizedSendDataDelegate<byte[]>? _getAdvertisementData;

        #region Module members

        [ConfigHelp<int>("Advertise", "SendMode", ConfigScope.Global, Default = 0, Min = 0, Max = 3,
            Description = "When to send a random advertisement (advert_#.bmp in the zone folder, or the subgame names b_ad#.bmp and r_ad#.bmp) to a player: 0 = never, 1 = on entering an arena, 2 = after being killed (at most once every 30 seconds), 3 = both.")]
        [ConfigHelp<int>("Advertise", "DisplayMode", ConfigScope.Global, Default = 2, Min = 0, Max = 255,
            Description = "Where the client draws advertisements: 0 or 2 = beneath AfterChat layer LVZ objects, 1 = above them.")]
        [ConfigHelp<int>("Advertise", "Duration", ConfigScope.Global, Default = 12000,
            Description = "How long the client shows an advertisement, in hundredths of a second. 0 or less is never shown.")]
        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            int sendMode = Math.Clamp(
                _configManager.GetInt(_configManager.Global, "Advertise", "SendMode", ConfigHelp.Constants.Global.Advertise.SendMode.Default),
                ConfigHelp.Constants.Global.Advertise.SendMode.Min,
                ConfigHelp.Constants.Global.Advertise.SendMode.Max);

            if (sendMode == 0)
            {
                // Nothing is loaded, watched or allocated.
                _logManager.LogM(LogLevel.Info, nameof(Advertisements), "Advertisements are off (Advertise:SendMode).");
                return true;
            }

            _displayMode = (byte)Math.Clamp(
                _configManager.GetInt(_configManager.Global, "Advertise", "DisplayMode", ConfigHelp.Constants.Global.Advertise.DisplayMode.Default),
                ConfigHelp.Constants.Global.Advertise.DisplayMode.Min,
                ConfigHelp.Constants.Global.Advertise.DisplayMode.Max);

            _duration = _configManager.GetInt(_configManager.Global, "Advertise", "Duration", ConfigHelp.Constants.Global.Advertise.Duration.Default);

            _isEnabled = true;
            _sendOnEnterArena = sendMode == 1 || sendMode == 3;
            _sendOnKilled = sendMode == 2 || sendMode == 3;
            _getAdvertisementData = GetAdvertisementData;

            // Only sending after being killed needs to know when the player was last sent one.
            if (_sendOnKilled)
                _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            _logManager.LogM(LogLevel.Info, nameof(Advertisements), $"Advertisements are on (send mode:{sendMode}, display mode:{_displayMode}, duration:{_duration}).");

            lock (_loadLock)
            {
                _loadCancellation = new CancellationTokenSource();
            }

            // Read the files again whenever one of them is added, changed, renamed or deleted.
            _fileSystemWatcher = new FileSystemWatcher(".");
            foreach (string prefix in FilePrefixes)
                _fileSystemWatcher.Filters.Add($"{prefix}*.bmp");

            _fileSystemWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
            _fileSystemWatcher.Changed += FileSystemWatcher_Changed;
            _fileSystemWatcher.Created += FileSystemWatcher_Changed;
            _fileSystemWatcher.Deleted += FileSystemWatcher_Changed;
            _fileSystemWatcher.Renamed += FileSystemWatcher_Changed;
            _fileSystemWatcher.EnableRaisingEvents = true;

            // The files are read on a worker thread. Waiting here does not block the server; it only means the module is done loading
            // once the first set of advertisements is ready.
            await QueueLoad().ConfigureAwait(false);

            if (_sendOnEnterArena)
                PlayerActionCallback.Register(_broker, Callback_PlayerAction);

            if (_sendOnKilled)
                KillCallback.Register(_broker, Callback_Kill);

            return true;
        }

        async Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (!_isEnabled)
                return true;

            if (_sendOnEnterArena)
                PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);

            if (_sendOnKilled)
                KillCallback.Unregister(_broker, Callback_Kill);

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

            // Stop the loader, if it is running, and wait for it so that it cannot replace the advertisements after they are cleared.
            CancellationTokenSource? loadCancellation;
            Task? loadTask;

            lock (_loadLock)
            {
                loadCancellation = _loadCancellation;
                _loadCancellation = null;
                loadTask = _loadTask;
            }

            if (loadCancellation is not null)
            {
                await loadCancellation.CancelAsync().ConfigureAwait(false);

                if (loadTask is not null)
                    await loadTask.ConfigureAwait(false);

                loadCancellation.Dispose();
            }

            if (_sendOnKilled)
                _playerData.FreePlayerData(ref _pdKey);

            _advertisements = [];
            _isEnabled = false;

            return true;
        }

        #endregion

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
        {
            if (action == PlayerAction.EnterArena)
                SendAdvertisement(player, true);
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
        {
            SendAdvertisement(killed, false);
        }

        /// <summary>
        /// Sends a random advertisement to a player.
        /// </summary>
        /// <param name="player">The player to send to.</param>
        /// <param name="isEnterArena">
        /// <see langword="true"/> to send it reliably, regardless of when the player was last sent one.
        /// <see langword="false"/> to send it as sized data, unless the player was sent one in the last 30 seconds.
        /// </param>
        private void SendAdvertisement(Player player, bool isEnterArena)
        {
            if (!player.IsStandard)
                return;

            // Take the current set; the loader may replace it at any time.
            byte[][] advertisements = _advertisements;
            if (advertisements.Length == 0)
                return; // none loaded

            if (_sendOnKilled)
            {
                if (!player.TryGetExtraData(_pdKey, out PlayerData? pd))
                    return;

                long now = Environment.TickCount64;
                long lastSent = Interlocked.Read(ref pd.LastSentTicks);
                if (!isEnterArena && lastSent != 0 && now - lastSent < KilledResendMilliseconds)
                    return;

                Interlocked.Exchange(ref pd.LastSentTicks, now);
            }

            byte[] advertisement = advertisements[Random.Shared.Next(advertisements.Length)];

            if (isEnterArena)
                _network.SendToOne(player, advertisement, NetSendFlags.Reliable);
            else
                _network.SendSized(player, advertisement.Length, _getAdvertisementData!, advertisement);
        }

        private void GetAdvertisementData(byte[] advertisement, int offset, Span<byte> dataSpan)
        {
            if (dataSpan.IsEmpty)
                return; // The transfer ended. The advertisement is shared, so there's nothing to clean up.

            ReadOnlySpan<byte> remaining = advertisement.AsSpan(offset);
            if (remaining.Length > dataSpan.Length)
                remaining = remaining[..dataSpan.Length];

            remaining.CopyTo(dataSpan);
        }

        private void FileSystemWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            QueueLoad();
        }

        /// <summary>
        /// Requests that the advertisement files be read, starting the loader on a worker thread if it is not already running.
        /// </summary>
        /// <remarks>
        /// Saving a file usually raises several change events. Requests that arrive while the loader is reading are combined
        /// into one more pass once it finishes, so the files are read at most twice for a burst of changes.
        /// </remarks>
        /// <returns>A task that completes when the loader has nothing left to do.</returns>
        private Task QueueLoad()
        {
            lock (_loadLock)
            {
                if (_loadCancellation is null)
                    return Task.CompletedTask; // unloading

                _isLoadRequested = true;
                CancellationToken cancellationToken = _loadCancellation.Token;
                return _loadTask ??= Task.Run(() => LoadLoopAsync(cancellationToken));
            }
        }

        private async Task LoadLoopAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_loadLock)
                {
                    if (!_isLoadRequested || cancellationToken.IsCancellationRequested)
                    {
                        _loadTask = null;
                        return;
                    }

                    _isLoadRequested = false;
                }

                try
                {
                    byte[][] advertisements = await LoadAdvertisementsAsync(cancellationToken).ConfigureAwait(false);
                    _advertisements = advertisements;

                    if (advertisements.Length == 0)
                        _logManager.LogM(LogLevel.Warn, nameof(Advertisements), "Advertise:SendMode is set, but no advertisements were found (advert_#.bmp, b_ad#.bmp, r_ad#.bmp).");
                    else
                        _logManager.LogM(LogLevel.Info, nameof(Advertisements), $"Loaded {advertisements.Length} advertisement(s).");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Unloading. The check at the top of the loop ends it.
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Advertisements), $"Error loading advertisements. {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Reads all of the advertisement files (prefix0.bmp to prefix127.bmp, for each prefix).
        /// </summary>
        /// <returns>The packets of the files that could be read.</returns>
        private async Task<byte[][]> LoadAdvertisementsAsync(CancellationToken cancellationToken)
        {
            List<byte[]> advertisements = [];
            byte[] header = new byte[BitmapHeaderLength]; // reused for each file

            foreach (string prefix in FilePrefixes)
            {
                for (int i = 0; i < MaxFilesPerPrefix; i++)
                {
                    string path = $"{prefix}{i}.bmp";
                    if (!File.Exists(path))
                        continue;

                    byte[]? packet;
                    string? error;

                    try
                    {
                        (packet, error) = await LoadFileAsync(path, header, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(Advertisements), $"Unable to read '{path}'. {ex.Message}");
                        continue;
                    }

                    if (packet is null)
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(Advertisements), $"Skipping '{path}': {error}");
                        continue;
                    }

                    advertisements.Add(packet);
                }
            }

            return [.. advertisements];
        }

        /// <summary>
        /// Opens an advertisement file and creates its packet.
        /// </summary>
        /// <remarks>
        /// A file that is in use (most likely still being written) is tried again a few times. If it was saved part way through,
        /// it is skipped as truncated, and the change event for the rest of the save reads it again.
        /// </remarks>
        private async Task<(byte[]? Packet, string? Error)> LoadFileAsync(string path, byte[] header, CancellationToken cancellationToken)
        {
            SafeFileHandle? handle = null;

            for (int tries = 1; handle is null; tries++)
            {
                try
                {
                    // Opening is a blocking call, but this runs on a worker thread.
                    handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
                }
                catch (IOException ex) when (ex is not FileNotFoundException && ex is not DirectoryNotFoundException && tries < MaxOpenTries)
                {
                    await Task.Delay(OpenRetryMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }

            using (handle)
            {
                return await CreatePacketAsync(handle, header, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Creates an S2C 0x30 packet from an uncompressed 8-bit bitmap file, reading the pixels straight into the packet.
        /// </summary>
        /// <param name="handle">The file, opened for asynchronous reads.</param>
        /// <param name="header">A buffer of <see cref="BitmapHeaderLength"/> bytes to read the bitmap header into.</param>
        /// <param name="cancellationToken">The token to observe for cancellation.</param>
        /// <returns>The packet, or <see langword="null"/> and the reason the file can't be used.</returns>
        private async Task<(byte[]? Packet, string? Error)> CreatePacketAsync(SafeFileHandle handle, byte[] header, CancellationToken cancellationToken)
        {
            // There's no need to get the file's length first: a read that comes up short means the file is truncated.
            if (!await TryReadExactlyAsync(handle, header, 0, cancellationToken).ConfigureAwait(false)
                || header[0] != (byte)'B'
                || header[1] != (byte)'M')
            {
                return (null, "not a bitmap file.");
            }

            // Not used after the awaits below, which an async method requires of a span local.
            ReadOnlySpan<byte> bmp = header;
            int pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(bmp[10..]);
            int infoHeaderSize = BinaryPrimitives.ReadInt32LittleEndian(bmp[14..]);
            int width = BinaryPrimitives.ReadInt32LittleEndian(bmp[18..]);
            int height = BinaryPrimitives.ReadInt32LittleEndian(bmp[22..]);
            ushort bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bmp[28..]);
            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(bmp[30..]);

            if (infoHeaderSize < 40 || bitsPerPixel != 8 || compression != 0)
                return (null, "only uncompressed 8-bit bitmaps are supported.");

            // A negative height means the rows are stored top row first; otherwise, bottom row first.
            bool isTopDown = height < 0;
            if (isTopDown)
                height = -height;

            if (width <= 0 || height <= 0 || width > ushort.MaxValue || height > ushort.MaxValue)
                return (null, $"invalid size ({width} x {height}).");

            long packetLength = HeaderLength + (long)width * height;
            if (packetLength > Constants.MaxBigPacket)
                return (null, $"too large ({width} x {height}); the image can be at most {Constants.MaxBigPacket - HeaderLength} pixels.");

            if (pixelOffset < BitmapHeaderLength)
                return (null, $"invalid pixel data offset ({pixelOffset}).");

            byte[] buffer = new byte[packetLength];
            buffer[0] = (byte)S2CPacketType.AdBanner;
            buffer[1] = _displayMode;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)height);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(6), _duration);

            // The packet holds the rows top row first. Each row is read straight into place at its offset in the file,
            // skipping the row padding, which is why the reads are positional (RandomAccess) rather than through a stream.
            int stride = (width + 3) & ~3; // rows are padded to 4 bytes
            Memory<byte> pixels = buffer.AsMemory(HeaderLength);
            for (int row = 0; row < height; row++)
            {
                int sourceRow = isTopDown ? row : height - 1 - row;
                if (!await TryReadExactlyAsync(handle, pixels.Slice(row * width, width), pixelOffset + (long)sourceRow * stride, cancellationToken).ConfigureAwait(false))
                    return (null, "the pixel data is truncated.");
            }

            return (buffer, null);
        }

        /// <summary>
        /// Reads from a file until <paramref name="buffer"/> is full.
        /// </summary>
        /// <returns><see langword="true"/> if the buffer was filled; <see langword="false"/> if the end of the file was reached first.</returns>
        private static async Task<bool> TryReadExactlyAsync(SafeFileHandle handle, Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken)
        {
            while (!buffer.IsEmpty)
            {
                int read = await RandomAccess.ReadAsync(handle, buffer, fileOffset, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    return false;

                buffer = buffer[read..];
                fileOffset += read;
            }

            return true;
        }

        private sealed class PlayerData : IResettable
        {
            /// <summary>
            /// When the player was last sent an advertisement (<see cref="Environment.TickCount64"/>), or 0 if never.
            /// </summary>
            public long LastSentTicks;

            bool IResettable.TryReset()
            {
                LastSentTicks = 0;
                return true;
            }
        }
    }
}

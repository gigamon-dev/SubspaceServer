using Microsoft.Extensions.ObjectPool;
using Microsoft.Win32.SafeHandles;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that sends zone advertisements: banner images that the client shows on screen for a while.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Advertisements are loaded from the zone's root folder when the module loads: <c>advert_0.bmp</c> to <c>advert_127.bmp</c>.
    /// The subgame names <c>b_ad0.bmp</c> to <c>b_ad127.bmp</c> and <c>r_ad0.bmp</c> to <c>r_ad127.bmp</c> are also loaded, so that a
    /// subgame zone's advertisements can be used as they are. All of them are treated alike. Each must be an uncompressed 8-bit bitmap.
    /// Its pixel values are sent as they are, as indexes into the client's own palette; the bitmap's palette is not used.
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
    /// The module does nothing unless Advertise:SendMode is set in global.conf (0 by default). While it is 0, it allocates nothing
    /// and loads no files. When on, each bitmap is read straight into its packet, and sending allocates nothing.
    /// Per-player data is only allocated when advertisements are sent after being killed, since only that needs the time of the last one.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public sealed class Advertisements(
        IComponentBroker broker,
        IConfigManager configManager,
        ILogManager logManager,
        INetwork network,
        IPlayerData playerData) : IModule
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

        private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        private PlayerDataKey<PlayerData> _pdKey;

        /// <summary>
        /// The loaded advertisements, each a complete S2C 0x30 packet. Empty while the module is off.
        /// </summary>
        private byte[][] _advertisements = [];

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
        bool IModule.Load(IComponentBroker broker)
        {
            int sendMode = Math.Clamp(
                _configManager.GetInt(_configManager.Global, "Advertise", "SendMode", ConfigHelp.Constants.Global.Advertise.SendMode.Default),
                ConfigHelp.Constants.Global.Advertise.SendMode.Min,
                ConfigHelp.Constants.Global.Advertise.SendMode.Max);

            if (sendMode == 0)
            {
                // Nothing is loaded or allocated.
                _logManager.LogM(LogLevel.Info, nameof(Advertisements), "Advertisements are off (Advertise:SendMode).");
                return true;
            }

            byte displayMode = (byte)Math.Clamp(
                _configManager.GetInt(_configManager.Global, "Advertise", "DisplayMode", ConfigHelp.Constants.Global.Advertise.DisplayMode.Default),
                ConfigHelp.Constants.Global.Advertise.DisplayMode.Min,
                ConfigHelp.Constants.Global.Advertise.DisplayMode.Max);

            int duration = _configManager.GetInt(_configManager.Global, "Advertise", "Duration", ConfigHelp.Constants.Global.Advertise.Duration.Default);

            // Collect into a rented array, then copy to one of the exact size.
            byte[]?[] loaded = ArrayPool<byte[]?>.Shared.Rent(FilePrefixes.Length * MaxFilesPerPrefix);
            int count = 0;

            try
            {
                foreach (string prefix in FilePrefixes)
                    LoadAdvertisements(prefix, displayMode, duration, loaded, ref count);

                if (count == 0)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(Advertisements), "Advertise:SendMode is set, but no advertisements were found (advert_#.bmp, b_ad#.bmp, r_ad#.bmp).");
                    return true;
                }

                _advertisements = new byte[count][];
                for (int i = 0; i < count; i++)
                    _advertisements[i] = loaded[i]!;
            }
            finally
            {
                ArrayPool<byte[]?>.Shared.Return(loaded, true);
            }

            _sendOnEnterArena = sendMode == 1 || sendMode == 3;
            _sendOnKilled = sendMode == 2 || sendMode == 3;
            _getAdvertisementData = GetAdvertisementData;

            // Only sending after being killed needs to know when the player was last sent one.
            if (_sendOnKilled)
                _pdKey = _playerData.AllocatePlayerData<PlayerData>();

            _logManager.LogM(LogLevel.Info, nameof(Advertisements), $"Loaded {_advertisements.Length} advertisement(s) (send mode:{sendMode}, display mode:{displayMode}, duration:{duration}).");

            if (_sendOnEnterArena)
                PlayerActionCallback.Register(_broker, Callback_PlayerAction);

            if (_sendOnKilled)
                KillCallback.Register(_broker, Callback_Kill);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (_advertisements.Length == 0)
                return true;

            if (_sendOnEnterArena)
                PlayerActionCallback.Unregister(_broker, Callback_PlayerAction);

            if (_sendOnKilled)
            {
                KillCallback.Unregister(_broker, Callback_Kill);
                _playerData.FreePlayerData(ref _pdKey);
            }

            _advertisements = [];

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

            byte[] advertisement = _advertisements[Random.Shared.Next(_advertisements.Length)];

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

        /// <summary>
        /// Loads the advertisement files with a given prefix (prefix0.bmp to prefix127.bmp).
        /// </summary>
        /// <param name="prefix">The file name prefix.</param>
        /// <param name="displayMode">The display mode to put in each packet.</param>
        /// <param name="duration">The duration to put in each packet.</param>
        /// <param name="advertisements">The array to add each loaded packet to.</param>
        /// <param name="count">The # of packets in <paramref name="advertisements"/>. Incremented for each one added.</param>
        private void LoadAdvertisements(string prefix, byte displayMode, int duration, byte[]?[] advertisements, ref int count)
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
                    using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (!TryCreatePacket(handle, displayMode, duration, out packet, out error))
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(Advertisements), $"Skipping '{path}': {error}");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(Advertisements), $"Unable to read '{path}'. {ex.Message}");
                    continue;
                }

                advertisements[count++] = packet;
            }
        }

        /// <summary>
        /// Creates an S2C 0x30 packet from an uncompressed 8-bit bitmap file, reading the pixels straight into the packet.
        /// </summary>
        private static bool TryCreatePacket(
            SafeFileHandle handle,
            byte displayMode,
            int duration,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? packet,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
        {
            packet = null;

            long fileLength = RandomAccess.GetLength(handle);
            Span<byte> header = stackalloc byte[BitmapHeaderLength];

            if (fileLength < BitmapHeaderLength
                || !TryReadExactly(handle, header, 0)
                || header[0] != (byte)'B'
                || header[1] != (byte)'M')
            {
                error = "not a bitmap file.";
                return false;
            }

            int pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(header[10..]);
            int infoHeaderSize = BinaryPrimitives.ReadInt32LittleEndian(header[14..]);
            int width = BinaryPrimitives.ReadInt32LittleEndian(header[18..]);
            int height = BinaryPrimitives.ReadInt32LittleEndian(header[22..]);
            ushort bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(header[30..]);

            if (infoHeaderSize < 40 || bitsPerPixel != 8 || compression != 0)
            {
                error = "only uncompressed 8-bit bitmaps are supported.";
                return false;
            }

            // A negative height means the rows are stored top row first; otherwise, bottom row first.
            bool isTopDown = height < 0;
            if (isTopDown)
                height = -height;

            if (width <= 0 || height <= 0 || width > ushort.MaxValue || height > ushort.MaxValue)
            {
                error = $"invalid size ({width} x {height}).";
                return false;
            }

            long packetLength = HeaderLength + (long)width * height;
            if (packetLength > Constants.MaxBigPacket)
            {
                error = $"too large ({width} x {height}); the image can be at most {Constants.MaxBigPacket - HeaderLength} pixels.";
                return false;
            }

            int stride = (width + 3) & ~3; // rows are padded to 4 bytes
            if (pixelOffset < 0 || pixelOffset + (long)stride * (height - 1) + width > fileLength)
            {
                error = "the pixel data is truncated.";
                return false;
            }

            byte[] buffer = new byte[packetLength];
            buffer[0] = (byte)S2CPacketType.AdBanner;
            buffer[1] = displayMode;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)height);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(6), duration);

            // The packet holds the rows top row first. Each row is read straight into place, skipping the row padding.
            Span<byte> pixels = buffer.AsSpan(HeaderLength);
            for (int row = 0; row < height; row++)
            {
                int sourceRow = isTopDown ? row : height - 1 - row;
                if (!TryReadExactly(handle, pixels.Slice(row * width, width), pixelOffset + (long)sourceRow * stride))
                {
                    error = "the pixel data is truncated.";
                    return false;
                }
            }

            packet = buffer;
            error = null;
            return true;
        }

        /// <summary>
        /// Reads from a file until <paramref name="buffer"/> is full.
        /// </summary>
        /// <returns><see langword="true"/> if the buffer was filled; <see langword="false"/> if the end of the file was reached first.</returns>
        private static bool TryReadExactly(SafeFileHandle handle, Span<byte> buffer, long fileOffset)
        {
            while (!buffer.IsEmpty)
            {
                int read = RandomAccess.Read(handle, buffer, fileOffset);
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

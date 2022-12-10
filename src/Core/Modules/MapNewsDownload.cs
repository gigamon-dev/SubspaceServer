using Ionic.Zlib;
using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality to download map files (lvl and lvz) and the news.txt file.
    /// </summary>
    [CoreModuleInfo]
    public sealed class MapNewsDownload : IModule, IMapNewsDownload, IDisposable
    {
        private ComponentBroker _broker;
        private IPlayerData _playerData;
        private INetwork _net;
        private ILogManager _logManager;
        private IConfigManager _configManager;
        private IMainloop _mainloop;
        private IArenaManager _arenaManager;
        private IMapData _mapData;
        private InterfaceRegistrationToken<IMapNewsDownload> _iMapNewsDownloadToken;

        private ArenaDataKey<List<MapDownloadData>> _dlKey;

        /// <summary>
        /// Map that's used if the configured one cannot be read.
        /// </summary>
        /// <remarks>
        /// This includes the header (0x2A, with filename "tinymap.lvl") and data (last 12 bytes).
        /// </remarks>
        private readonly byte[] _emergencyMap = new byte[]
        {
            0x2a, 0x74, 0x69, 0x6e, 0x79, 0x6d, 0x61, 0x70,
            0x2e, 0x6c, 0x76, 0x6c, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x78, 0x9c, 0x63, 0x60, 0x60, 0x60, 0x04,
            0x00, 0x00, 0x05, 0x00, 0x02
        };

        /// <summary>
        /// manages news.txt
        /// </summary>
        private NewsManager _newsManager;

        #region Module Members

        [ConfigHelp("General", "NewsFile", ConfigScope.Global, typeof(string), DefaultValue = "news.txt",
            Description = "The filename of the news file.")]
        public bool Load(
            ComponentBroker broker,
            IPlayerData playerData,
            INetwork net,
            ILogManager logManager,
            IConfigManager configManager,
            IMainloop mainloop,
            IArenaManager arenaManager,
            IMapData mapData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));

            _dlKey = _arenaManager.AllocateArenaData(new MapDownloadDataListPooledObjectPolicy());

            _net.AddPacket(C2SPacketType.UpdateRequest, Packet_UpdateRequest);
            _net.AddPacket(C2SPacketType.MapRequest, Packet_MapNewsRequest);
            _net.AddPacket(C2SPacketType.NewsRequest, Packet_MapNewsRequest);

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);

            string newsFilename = _configManager.GetStr(_configManager.Global, "General", "NewsFile");
            if (string.IsNullOrWhiteSpace(newsFilename))
                newsFilename = "news.txt";

            _newsManager = new NewsManager(this, newsFilename);

            _iMapNewsDownloadToken = broker.RegisterInterface<IMapNewsDownload>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iMapNewsDownloadToken) != 0)
                return false;

            _newsManager.Dispose();

            _net.RemovePacket(C2SPacketType.UpdateRequest, Packet_UpdateRequest);
            _net.RemovePacket(C2SPacketType.MapRequest, Packet_MapNewsRequest);
            _net.RemovePacket(C2SPacketType.NewsRequest, Packet_MapNewsRequest);

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);

            _arenaManager.FreeArenaData(_dlKey);

            return true;
        }

        #endregion

        #region IMapNewsDownload Members

        void IMapNewsDownload.SendMapFilename(Player player)
        {
            if (player == null)
                return;

            Arena arena = player.Arena;
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_dlKey, out List<MapDownloadData> downloadList))
                return;

            if (downloadList.Count == 0)
            {
                _logManager.LogA(LogLevel.Warn, nameof(MapNewsDownload), arena, "Missing map data.");
                return;
            }

            S2C_MapFilename packet = new();

            int len = 0;

            // allow vie clients that specifically ask for them to get all the
            // lvz data, to support bots.
            if (player.Type == ClientType.Continuum || player.Flags.WantAllLvz)
            {
                int idx = 0;

                foreach (MapDownloadData data in downloadList)
                {
                    if (!data.optional || player.Flags.WantAllLvz)
                    {
                        len = packet.SetFileInfo(idx, data.filename, data.checksum, data.cmplen);
                        idx++;
                    }
                }
            }
            else
            {
                MapDownloadData data = downloadList[0]; // index 0 always has the lvl
                len = packet.SetFileInfo(data.filename, data.checksum);
            }

            Debug.Assert(len > 0);

            _net.SendToOne(
                player, 
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1))[..len], 
                NetSendFlags.Reliable);
        }

        uint IMapNewsDownload.GetNewsChecksum() => _newsManager.TryGetNews(out _, out uint checksum) ? checksum : 0;

        #endregion

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null)
                return;

            if (action == ArenaAction.Create)
            {
                // note: asss does this in reverse order, but i think it is a race condition
                _arenaManager.HoldArena(arena);
                _mainloop.QueueThreadPoolWorkItem(ArenaActionWork, arena);
            }
            else if (action == ArenaAction.Destroy)
            {
                if (!arena.TryGetExtraData(_dlKey, out List<MapDownloadData> downloadList))
                    return;

                downloadList.Clear();
            }

            void ArenaActionWork(Arena arena)
            {
                if (arena == null)
                    return;

                try
                {
                    if (!arena.TryGetExtraData(_dlKey, out List<MapDownloadData> downloadList))
                        return;

                    downloadList.Clear();

                    MapDownloadData data = null;

                    string filename = _mapData.GetMapFilename(arena, null);
                    if (!string.IsNullOrEmpty(filename))
                    {
                        data = CompressMap(filename, true);
                    }

                    if (data == null)
                    {
                        _logManager.LogA(LogLevel.Warn, nameof(MapNewsDownload), arena, "Can't load level file, falling back to 'tinymap.lvl'.");
                        data = new MapDownloadData();
                        data.checksum = 0x5643ef8a;
                        data.uncmplen = 4;
                        data.cmplen = (uint)_emergencyMap.Length;
                        data.cmpmap = _emergencyMap;
                        data.filename = "tinymap.lvl";
                    }

                    downloadList.Add(data);

                    // now look for lvzs
                    foreach (LvzFileInfo lvzInfo in _mapData.LvzFilenames(arena))
                    {
                        data = CompressMap(lvzInfo.Filename, false);

                        if (data != null)
                        {
                            data.optional = lvzInfo.IsOptional;
                            downloadList.Add(data);
                        }
                    }
                }
                finally
                {
                    _arenaManager.UnholdArena(arena);
                }
            }
        }

        private MapDownloadData CompressMap(string filename, bool docomp)
        { 
            if (string.IsNullOrWhiteSpace(filename))
                throw new ArgumentException("Cannot be null or white-space.", nameof(filename));

            try
            {
                MapDownloadData mdd = new();

                string mapname = Path.GetFileName(filename);
                if (mapname.Length > 20)
                    mapname = mapname[..20]; // TODO: ASSS uses 20 as the max in MapDownloadData, but MapFilenamePacket has a limit of 16? Perhaps ".lvl" does not need to be included in the packet?

                mdd.filename = mapname;

                byte[] mapData;

                using (FileStream inputStream = File.OpenRead(filename))
                {
                    mdd.uncmplen = (uint)inputStream.Length;

                    // calculate CRC
                    Ionic.Crc.CRC32 crc32 = new();
                    mdd.checksum = (uint)crc32.GetCrc32(inputStream);

                    inputStream.Position = 0;

                    if (docomp)
                    {
                        // compress using zlib
                        using MemoryStream compressedStream = new();
                        using (ZlibStream zlibStream = new(
                            compressedStream,
                            CompressionMode.Compress,
                            CompressionLevel.Default))
                        {
                            inputStream.CopyTo(zlibStream);
                        }

                        mapData = compressedStream.ToArray();
                    }
                    else
                    {
                        // read data into a byte array
                        using MemoryStream ms = new((int)inputStream.Length);
                        inputStream.CopyTo(ms);
                        mapData = ms.ToArray();
                    }
                }

                mdd.cmpmap = new byte[17 + mapData.Length];

                // set up the packet header
                mdd.cmpmap[0] = (byte)S2CPacketType.MapData;
                Encoding.ASCII.GetBytes(mapname, 0, (mapname.Length <= 16) ? mapname.Length : 16, mdd.cmpmap, 1);

                // and the data
                Array.Copy(mapData, 0, mdd.cmpmap, 17, mapData.Length);
                mdd.cmplen = (uint)mdd.cmpmap.Length;

                if (mdd.cmpmap.Length > 256 * 1024)
                    _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), $"Compressed map/lvz is bigger than 256k: {filename}.");

                return mdd;
            }
            catch
            {
                return null;
            }
        }

        private void Packet_UpdateRequest(Player player, byte[] pkt, int len)
        {
            if (player == null)
                return;

            if (pkt == null)
                return;

            if (len != 1)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(MapNewsDownload), player, $"Bad update req packet len={len}.");
                return;
            }

            IFileTransfer fileTransfer = _broker.GetInterface<IFileTransfer>();
            if (fileTransfer != null)
            {
                try
                {
                    if (!fileTransfer.SendFile(player, "clients/update.exe", string.Empty, false))
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), "Update request, but error setting up to be sent.");
                    }
                }
                finally
                {
                    _broker.ReleaseInterface(ref fileTransfer);
                }
            }
        }

        private void Packet_MapNewsRequest(Player player, byte[] pkt, int len)
        {
            if (player == null)
                return;

            if (pkt == null)
                return;

            if (pkt[0] == (byte)C2SPacketType.MapRequest)
            {
                if (len != 1 && len != 3)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(MapNewsDownload), player, $"Bad map/LVZ req packet len={len}.");
                    return;
                }

                Arena arena = player.Arena;
                if (arena == null)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(MapNewsDownload), player, "Map request before entering arena.");
                    return;
                }

                ushort lvznum = (len == 3) ? (ushort)(pkt[1] | pkt[2] << 8) : (ushort)0;
                bool wantOpt = player.Flags.WantAllLvz;

                MapDownloadData mdd = GetMap(arena, lvznum, wantOpt);

                if (mdd == null)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(MapNewsDownload), player, $"Can't find lvl/lvz {lvznum}.");
                    return;
                }

                _net.SendSized(player, (int)mdd.cmplen, GetData, new MapDownloadContext(player, mdd));

                _logManager.LogP(LogLevel.Drivel, nameof(MapNewsDownload), player, $"Sending map/lvz {lvznum} ({mdd.cmplen} bytes) (transfer '{mdd.filename}').");

                // if we're getting these requests, it's too late to set their ship
                // and team directly, we need to go through the in-game procedures
                if (player.IsStandard && (player.Ship != ShipType.Spec || player.Freq != arena.SpecFreq))
                {
                    IGame game = _broker.GetInterface<IGame>();
                    if (game != null)
                    {
                        try
                        {
                            game.SetShipAndFreq(player, ShipType.Spec, arena.SpecFreq);
                        }
                        finally
                        {
                            _broker.ReleaseInterface(ref game);
                        }
                    }
                }
            }
            else if (pkt[0] == (byte)C2SPacketType.NewsRequest)
            {
                if (len != 1)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(MapNewsDownload), player, $"Bad news req packet len={len}.");
                    return;
                }

                if (_newsManager.TryGetNews(out byte[] compressedNewsData, out _))
                {
                    _net.SendSized(player, compressedNewsData.Length, GetData, new NewsDownloadContext(player, compressedNewsData));
                    _logManager.LogP(LogLevel.Drivel, nameof(MapNewsDownload), player, $"Sending news.txt ({compressedNewsData.Length} bytes).");
                }
                else
                {
                    _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), "News request, but compressed news doesn't exist.");
                }
            }
        }

        private MapDownloadData GetMap(Arena arena, int lvznum, bool wantOpt)
        {
            if (!arena.TryGetExtraData(_dlKey, out List<MapDownloadData> downloadList))
                return null;

            int idx=lvznum;
            foreach(MapDownloadData mdd in downloadList)
            {
                if (!mdd.optional || wantOpt)
                {
                    if (idx == 0)
                        return mdd;

                    idx--;
                }
            }

            return null;
        }

        private void GetData(NewsDownloadContext context, int offset, Span<byte> dataSpan)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Cannot be less than zero.");

            if (dataSpan.IsEmpty)
            {
                _logManager.LogP(LogLevel.Drivel, nameof(MapNewsDownload), context.Player, "Finished news download.");
                return;
            }

            byte[] newsData = context.NewsData;

            int bytesToCopy = dataSpan.Length;
            if (newsData.Length - offset < bytesToCopy)
            {
                bytesToCopy = newsData.Length - offset;

                if (bytesToCopy <= 0)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), $"Needed to retrieve news sized data of {dataSpan.Length} bytes, but there was no data remaining.");
                    return;
                }
                
                _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), $"Needed to retrieve news sized data of {dataSpan.Length} bytes, but there was only {bytesToCopy} bytes.");
            }

            new Span<byte>(newsData, offset, bytesToCopy).CopyTo(dataSpan);
        }

        private void GetData(MapDownloadContext context, int offset, Span<byte> dataSpan)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            MapDownloadData mdd = context.MapDownloadData;

            if (dataSpan.IsEmpty)
            {
                _logManager.LogP(LogLevel.Drivel, nameof(MapNewsDownload), context.Player, $"Finished map/lvz download (transfer '{mdd.filename}').");
                return;
            }

            int bytesToCopy = dataSpan.Length;
            if (mdd.cmpmap.Length - offset < bytesToCopy)
            {
                bytesToCopy = mdd.cmpmap.Length - offset;

                if (bytesToCopy <= 0)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), $"Needed to retrieve map sized data of {dataSpan.Length} bytes, but there was no data remaining.");
                    return;
                }
                
                _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), $"Needed to retrieve map sized data of {dataSpan.Length} bytes, but there was only {bytesToCopy} bytes.");
            }

            new Span<byte>(mdd.cmpmap, offset, bytesToCopy).CopyTo(dataSpan);
        }

        public void Dispose()
        {
            _newsManager?.Dispose();
        }

        #region Helper types

        private class NewsDownloadContext
        {
            public NewsDownloadContext(Player player, byte[] newsData)
            {
                Player = player ?? throw new ArgumentNullException(nameof(player));
                NewsData = newsData ?? throw new ArgumentNullException(nameof(newsData));
            }

            public Player Player { get; }
            public byte[] NewsData { get; }
        }

        private class MapDownloadContext
        {
            public MapDownloadContext(Player player, MapDownloadData mapDownloadData)
            {
                Player = player ?? throw new ArgumentNullException(nameof(player));
                MapDownloadData = mapDownloadData ?? throw new ArgumentNullException(nameof(mapDownloadData));
            }

            public Player Player { get; }
            public MapDownloadData MapDownloadData { get; }
        }

        private sealed class NewsManager : IDisposable
        {
            private readonly MapNewsDownload _parent;
            private readonly string _newsFilename;
            private byte[] _compressedNewsData; // includes packet header
            private uint? _newsChecksum;
            private FileSystemWatcher _fileWatcher;
            private readonly ReaderWriterLockSlim _rwLock = new();

            public NewsManager(MapNewsDownload parent, string filename)
            {
                _parent = parent ?? throw new ArgumentNullException(nameof(parent));

                if (string.IsNullOrWhiteSpace(filename))
                    throw new ArgumentException("Cannot be null or white-space.", nameof(filename));

                _newsFilename = filename;

                _fileWatcher = new FileSystemWatcher(".", _newsFilename);
                _fileWatcher.NotifyFilter = NotifyFilters.LastWrite;
                _fileWatcher.Changed += FileWatcher_Changed;
                _fileWatcher.EnableRaisingEvents = true;

                ProcessNewsFile();
            }

            private void FileWatcher_Changed(object sender, FileSystemEventArgs e)
            {
                ProcessNewsFile();
            }

            private void ProcessNewsFile()
            {
                _rwLock.EnterUpgradeableReadLock();

                try
                {
                    uint checksum;
                    byte[] compressedData;

                    try
                    {
                        FileStream newsStream = null;
                        int tries = 0;

                        do
                        {
                            try
                            {
                                newsStream = File.OpenRead(_newsFilename);
                            }
                            catch (IOException ex)
                            {
                                // Note: This retry logic is to workaround the "The process cannot access the file because it is being used by another process." race condition.
                                if (++tries >= 30)
                                {
                                    _parent._logManager.LogM(LogLevel.Error, nameof(MapNewsDownload), $"Error opening '{_newsFilename}' ({tries} tries). {ex.Message}");
                                    return;
                                }

                                _parent._logManager.LogM(LogLevel.Drivel, nameof(MapNewsDownload), $"Error opening '{_newsFilename}' ({tries} tries). {ex.Message}");

                                Thread.Sleep(100);
                            }
                            catch (Exception ex)
                            {
                                _parent._logManager.LogM(LogLevel.Error, nameof(MapNewsDownload), $"Error opening '{_newsFilename}'. {ex.Message}");
                                return;
                            }
                        }
                        while (newsStream == null);

                        try
                        {
                            // calculate the checksum
                            Ionic.Crc.CRC32 crc32 = new();
                            checksum = (uint)crc32.GetCrc32(newsStream);

                            if (_newsChecksum != null && _newsChecksum.Value == checksum)
                            {
                                _parent._logManager.LogM(LogLevel.Drivel, nameof(MapNewsDownload), $"Checked '{_newsFilename}', but there was no change (checksum {checksum:X}).");
                                return; // same checksum, no change
                            }

                            newsStream.Position = 0;

                            // compress using zlib
                            using MemoryStream compressedStream = new();
                            using (ZlibStream zlibStream = new(
                                compressedStream,
                                CompressionMode.Compress,
                                CompressionLevel.Default)) // Note: Had issues when it was CompressionLevel.BestCompression, contiuum didn't decrypt
                            {
                                newsStream.CopyTo(zlibStream);
                            }

                            compressedData = compressedStream.ToArray();
                        }
                        finally
                        {
                            newsStream.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        _parent._logManager.LogM(LogLevel.Drivel, nameof(MapNewsDownload), $"Error loading '{_newsFilename}'. {ex.Message}");
                        return;
                    }

                    // prepare the file packet
                    byte[] fileData = new byte[17 + compressedData.Length]; // 17 is the size of the header
                    fileData[0] = (byte)S2CPacketType.IncomingFile;
                    // intentionally leaving 16 bytes of 0 for the name
                    Array.Copy(compressedData, 0, fileData, 17, compressedData.Length);

                    // update the data members
                    _rwLock.EnterWriteLock();

                    try
                    {
                        _compressedNewsData = fileData;
                        _newsChecksum = checksum;
                    }
                    finally
                    {
                        _rwLock.ExitWriteLock();
                    }

                    _parent._logManager.LogM(LogLevel.Info, nameof(MapNewsDownload), $"Loaded news.txt (checksum {checksum:X}), compressed as {compressedData.Length} bytes.");
                }
                finally
                {
                    _rwLock.ExitUpgradeableReadLock();
                }
            }

            public bool TryGetNews(out byte[] data, out uint checksum)
            {
                _rwLock.EnterReadLock();

                try
                {
                    if (_compressedNewsData != null && _newsChecksum != null)
                    {
                        data = _compressedNewsData;
                        checksum = _newsChecksum.Value;
                        return true;
                    }
                }
                finally
                {
                    _rwLock.ExitReadLock();
                }

                data = null;
                checksum = 0;
                return false;
            }

            #region IDisposable Members

            public void Dispose()
            {
                if (_fileWatcher != null)
                {
                    _fileWatcher.EnableRaisingEvents = false;
                    _fileWatcher.Changed -= FileWatcher_Changed;
                    _fileWatcher.Dispose();
                    _fileWatcher = null;
                }

                _rwLock.Dispose();
            }

            #endregion
        }

        private class MapDownloadData
        {
            /// <summary>
            /// crc32 of file
            /// </summary>
            public uint checksum;

            /// <summary>
            /// uncompressed length
            /// </summary>
            public uint uncmplen;

            /// <summary>
            /// compressed length
            /// </summary>
            public uint cmplen;

            /// <summary>
            /// if the file is optional (lvzs are optional)
            /// </summary>
            public bool optional;

            /// <summary>
            /// compressed bytes of the file, includes the packet header
            /// </summary>
            public byte[] cmpmap;

            /// <summary>
            /// name of the file
            /// </summary>
            public string filename;

            public override string ToString()
            {
                return filename;
            }
        }

        private class MapDownloadDataListPooledObjectPolicy : PooledObjectPolicy<List<MapDownloadData>>
        {
            public int InitialCapacity { get; set; } = S2C_MapFilename.MaxFiles;

            public override List<MapDownloadData> Create()
            {
                return new List<MapDownloadData>(InitialCapacity);
            }

            public override bool Return(List<MapDownloadData> obj)
            {
                if (obj == null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        #endregion
    }
}

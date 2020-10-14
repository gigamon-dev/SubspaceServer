using Ionic.Zlib;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class MapNewsDownload : IModule, IMapNewsDownload, IDisposable
    {
        private ComponentBroker _broker;
        private IPlayerData _playerData;
        private INetwork _net;
        private ILogManager _logManager;
        private IConfigManager _configManager;
        private IMainloop _mainloop;
        private IArenaManager _arenaManager;
        private IMapData _mapData;
        private InterfaceRegistrationToken _iMapNewsDownloadToken;

        private int _dlKey;

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

        private class NewsManager : IDisposable
        {
            private readonly string _newsFilename;
            private byte[] _compressedNewsData; // includes packet header
            private uint _newsChecksum;
            private FileSystemWatcher _fileWatcher;
            private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim();

            public NewsManager(string filename)
            {
                if (string.IsNullOrEmpty(filename))
                    throw new ArgumentNullException("filename");

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
                uint checksum;
                byte[] compressedData;

                using(FileStream newsStream = File.OpenRead(_newsFilename))
                {
                    // calculate the checksum
                    Ionic.Crc.CRC32 crc32 = new Ionic.Crc.CRC32();
                    checksum = (uint)crc32.GetCrc32(newsStream);

                    newsStream.Position = 0;

                    // compress using zlib
                    using MemoryStream compressedStream = new MemoryStream();
                    using (ZlibStream zlibStream = new ZlibStream(
                        compressedStream,
                        CompressionMode.Compress,
                        CompressionLevel.Default)) // Note: Had issues when it was CompressionLevel.BestCompression, contiuum didn't decrypt
                    {
                        newsStream.CopyTo(zlibStream);
                    }

                    compressedData = compressedStream.ToArray();
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
            }

            public bool TryGetNews(out byte[] data, out uint checksum)
            {
                _rwLock.EnterReadLock();

                try
                {
                    if (_compressedNewsData != null)
                    {
                        data = _compressedNewsData;
                        checksum = _newsChecksum;
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
            }

            #endregion
        }

        /// <summary>
        /// manages news.txt
        /// </summary>
        private NewsManager _newsManager;

        #region IModule Members

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

            _dlKey = _arenaManager.AllocateArenaData<LinkedList<MapDownloadData>>();

            _net.AddPacket(C2SPacketType.UpdateRequest, Packet_UpdateRequest);
            _net.AddPacket(C2SPacketType.MapRequest, Packet_MapNewsRequest);
            _net.AddPacket(C2SPacketType.NewsRequest, Packet_MapNewsRequest);

            ArenaActionCallback.Register(_broker, Callback_ArenaAction);

            string newsFilename = _configManager.GetStr(_configManager.Global, "General", "NewsFile");
            if (string.IsNullOrEmpty(newsFilename))
                newsFilename = "news.txt";

            _newsManager = new NewsManager(newsFilename);

            _iMapNewsDownloadToken = broker.RegisterInterface<IMapNewsDownload>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface<IMapNewsDownload>(ref _iMapNewsDownloadToken) != 0)
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

        public void SendMapFilename(Player p)
        {
            if (p == null)
                return;

            Arena arena = p.Arena;
            if (arena == null)
                return;

            if (!(arena[_dlKey] is LinkedList<MapDownloadData> dls))
                return;

            if (dls.Count == 0)
            {
                _logManager.LogA(LogLevel.Warn, nameof(MapNewsDownload), arena, "missing map data");
                return;
            }

            using DataBuffer buffer = Pool<DataBuffer>.Default.Get();
            MapFilenamePacket mf = new MapFilenamePacket(buffer.Bytes);
            mf.Initialize();

            int len = 0;

            // allow vie clients that specifically ask for them to get all the
            // lvz data, to support bots.
            if (p.Type == ClientType.Continuum || p.Flags.WantAllLvz)
            {
                int idx = 0;

                foreach (MapDownloadData data in dls)
                {
                    if (!data.optional || p.Flags.WantAllLvz)
                    {
                        len = mf.SetFileInfo(idx, data.filename, data.checksum, data.cmplen);
                        idx++;
                    }
                }
            }
            else
            {
                MapDownloadData data = dls.First.Value;
                len = mf.SetFileInfo(0, data.filename, data.checksum, null);
            }

            Debug.Assert(len > 0);

            _net.SendToOne(p, buffer.Bytes, len, NetSendFlags.Reliable);
        }

        public uint GetNewsChecksum() => _newsManager.TryGetNews(out _, out uint checksum) ? checksum : 0;

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
                if (!(arena[_dlKey] is LinkedList<MapDownloadData> dls))
                    return;

                dls.Clear();
            }
        }

        private void ArenaActionWork(Arena arena)
        {
            if (arena == null)
                return;

            try
            {
                if (!(arena[_dlKey] is LinkedList<MapDownloadData> dls))
                    return;

                MapDownloadData data = null;

                string filename = _mapData.GetMapFilename(arena, null);
                if (!string.IsNullOrEmpty(filename))
                {
                    data = CompressMap(filename, true);
                }

                if (data == null)
                {
                    _logManager.LogA(LogLevel.Warn, nameof(MapNewsDownload), arena, "can't load level file, falling back to tinymap.lvl");
                    data = new MapDownloadData();
                    data.checksum = 0x5643ef8a;
                    data.uncmplen = 4;
                    data.cmplen = (uint)_emergencyMap.Length;
                    data.cmpmap = _emergencyMap;
                    data.filename = "tinymap.lvl";
                }

                dls.AddLast(data);

                // now look for lvzs
                foreach(LvzFileInfo lvzInfo in _mapData.LvzFilenames(arena))
                {
                    data = CompressMap(lvzInfo.Filename, false);

                    if (data != null)
                    {
                        data.optional = lvzInfo.IsOptional;
                        dls.AddLast(data);
                    }
                }
            }
            finally
            {
                _arenaManager.UnholdArena(arena);
            }
        }

        private MapDownloadData CompressMap(string filename, bool docomp)
        {
            if (string.IsNullOrWhiteSpace(filename))
                throw new ArgumentException("Cannot be null or white-space.", nameof(filename));

            try
            {
                MapDownloadData mdd = new MapDownloadData();

                string mapname = Path.GetFileName(filename);
                if (mapname.Length > 20)
                    mapname = mapname.Substring(0, 20); // TODO: ASSS uses 20 as the max in MapDownloadData, but MapFilenamePacket has a limit of 16? Perhaps ".lvl" does not need to be included in the packet?

                mdd.filename = mapname;

                byte[] mapData;

                using (FileStream inputStream = File.OpenRead(filename))
                {
                    mdd.uncmplen = (uint)inputStream.Length;

                    // calculate CRC
                    Ionic.Crc.CRC32 crc32 = new Ionic.Crc.CRC32();
                    mdd.checksum = (uint)crc32.GetCrc32(inputStream);

                    inputStream.Position = 0;

                    if (docomp)
                    {
                        // compress using zlib
                        using MemoryStream compressedStream = new MemoryStream();
                        using (ZlibStream zlibStream = new ZlibStream(
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
                        using MemoryStream ms = new MemoryStream((int)inputStream.Length);
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
                    _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), "compressed map/lvz is bigger than 256k: {0}", filename);

                return mdd;
            }
            catch
            {
                return null;
            }
        }

        private void Packet_UpdateRequest(Player p, byte[] pkt, int len)
        {
            if (p == null)
                return;

            if (pkt == null)
                return;

            if (len != 1)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(MapNewsDownload), p, "bad update req packet len={0}", len);
                return;
            }

            _logManager.LogP(LogLevel.Drivel, nameof(MapNewsDownload), p, "UpdateRequest");

            IFileTransfer fileTransfer = _broker.GetInterface<IFileTransfer>();
            if (fileTransfer != null)
            {
                try
                {
                    if (!fileTransfer.SendFile(p, "clients/update.exe", string.Empty, false))
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), "update request, but error setting up to be sent");
                    }
                }
                finally
                {
                    _broker.ReleaseInterface(ref fileTransfer);
                }
            }
        }

        private void Packet_MapNewsRequest(Player p, byte[] pkt, int len)
        {
            if (p == null)
                return;

            if (pkt == null)
                return;

            if (pkt[0] == (byte)C2SPacketType.MapRequest)
            {
                if (len != 1 && len != 3)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(MapNewsDownload), p, "bad map/LVZ req packet len={0}", len);
                    return;
                }

                Arena arena = p.Arena;
                if (arena == null)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(MapNewsDownload), p, "map request before entering arena");
                    return;
                }

                ushort lvznum = (len == 3) ? (ushort)(pkt[1] | pkt[2] << 8) : (ushort)0;
                bool wantOpt = p.Flags.WantAllLvz;

                MapDownloadData mdd = GetMap(arena, lvznum, wantOpt);

                if (mdd == null)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(MapNewsDownload), p, "can't find lvl/lvz {0}", lvznum);
                    return;
                }

                _net.SendSized(p, (int)mdd.cmplen, GetData, new MapDownloadContext(p, mdd));

                _logManager.LogP(LogLevel.Drivel, nameof(MapNewsDownload), p, "sending map/lvz {0} ({1} bytes) (transfer '{2}')", lvznum, mdd.cmplen, mdd.filename);

                // if we're getting these requests, it's too late to set their ship
                // and team directly, we need to go through the in-game procedures
                if (p.IsStandard && (p.Ship != ShipType.Spec || p.Freq != arena.SpecFreq))
                {
                    IGame game = _broker.GetInterface<IGame>();
                    if (game != null)
                    {
                        try
                        {
                            game.SetShipAndFreq(p, ShipType.Spec, arena.SpecFreq);
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
                    _logManager.LogP(LogLevel.Malicious, nameof(MapNewsDownload), p, "bad news req packet len={0}", len);
                    return;
                }

                if (_newsManager.TryGetNews(out byte[] compressedNewsData, out _))
                {
                    _net.SendSized(p, compressedNewsData.Length, GetData, new NewsDownloadContext(p, compressedNewsData));
                    _logManager.LogP(LogLevel.Drivel, nameof(MapNewsDownload), p, "sending news.txt ({0} bytes)", compressedNewsData.Length);
                }
                else
                {
                    _logManager.LogM(LogLevel.Warn, nameof(MapNewsDownload), "news request, but compressed news doesn't exist");
                }
            }
        }

        private MapDownloadData GetMap(Arena arena, int lvznum, bool wantOpt)
        {
            if (!(arena[_dlKey] is LinkedList<MapDownloadData> dls))
                return null;

            int idx=lvznum;
            foreach(MapDownloadData mdd in dls)
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
                _logManager.LogP(LogLevel.Drivel, nameof(MapNewsDownload), context.Player, "Finished map/lvz download (transfer '{0}')", mdd.filename);
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
            if (_newsManager != null)
            {
                _newsManager.Dispose();
            }
        }

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
    }
}

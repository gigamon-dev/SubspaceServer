using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SS.Core.ComponentInterfaces;
using SS.Core.ComponentCallbacks;
using SS.Core.Packets;
using SS.Utilities;

namespace SS.Core.Modules
{
    public class MapNewsDownload : IModule, IMapNewsDownload
    {
        private ModuleManager _mm;
        private IPlayerData _playerData;
        private INetwork _net;
        private ILogManager _logManager;
        private IConfigManager _configManager;
        private IServerTimer _mainLoop;
        private IArenaManagerCore _arenaManager;
        private IMapData _mapData;

        private int _dlKey;

        private byte[] _emergencyMap = new byte[]
        {
            0x2a, 0x74, 0x69, 0x6e, 0x79, 0x6d, 0x61, 0x70,
            0x2e, 0x6c, 0x76, 0x6c, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x78, 0x9c, 0x63, 0x60, 0x60, 0x60, 0x04,
            0x00, 0x00, 0x05, 0x00, 0x02
        };

        private static class ZlibNativeMethods
        {
            [DllImport("zlib1.dll", EntryPoint = "compress", CallingConvention = CallingConvention.Cdecl)]
            public static extern int Compress(byte[] dest, ref uint destLen, byte[] source, uint sourceLen);

            [DllImport("zlib1.dll", EntryPoint = "crc32", CallingConvention = CallingConvention.Cdecl)]
            public static extern uint Crc32(uint crc, byte[] buf, uint len);
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

        private class DataLocator
        {
            public Arena Arena;
            public int LvzNum;
            public bool WantOpt;
            public uint Len;

            public DataLocator(Arena arena, int lvzNum, bool wantOpt, uint len)
            {
                Arena = arena;
                LvzNum = lvzNum;
                WantOpt = wantOpt;
                Len = len;
            }

            public override string ToString()
            {
                StringBuilder sb = new StringBuilder();
                if(Arena != null)
                {
                    sb.Append(Arena.Name);
                    sb.Append(':');
                }
                sb.Append(LvzNum);
                return sb.ToString();
            }
        }

        private class NewsManager : IDisposable
        {
            private string _newsFilename;
            private byte[] _compressedNewsData; // includes packet header
            private uint _newsChecksum;
            private FileSystemWatcher _fileWatcher;
            private ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

            public NewsManager(string filename)
            {
                if (string.IsNullOrEmpty(filename))
                    throw new ArgumentNullException("filename");

                _newsFilename = filename;

                _fileWatcher = new FileSystemWatcher(".", _newsFilename);
                _fileWatcher.NotifyFilter = NotifyFilters.LastWrite;
                _fileWatcher.Changed += new FileSystemEventHandler(_fileWatcher_Changed);
                _fileWatcher.EnableRaisingEvents = true;

                processNewsFile();
            }

            private void _fileWatcher_Changed(object sender, FileSystemEventArgs e)
            {
                processNewsFile();
            }

            private void processNewsFile()
            {
                byte[] fileData = File.ReadAllBytes(_newsFilename);

                // calculate the checksum
                CRC32 crc32 = new CRC32();
                uint checksum;
                using (MemoryStream ms = new MemoryStream(fileData))
                {
                    checksum = crc32.GetCrc32(ms);
                }

                // compress the data
                byte[] compressedData = new byte[(int)(1.0011 * fileData.Length + 35)];
                uint destLen = (uint)compressedData.Length;
                ZlibNativeMethods.Compress(compressedData, ref destLen, fileData, (uint)fileData.Length);
                fileData = new byte[destLen + 17]; // 17 is the size of the header
                fileData[0] = (byte)S2CPacketType.IncomingFile;
                // intentionally leaving 16 bytes of 0 for the name
                Array.Copy(compressedData, 0, fileData, 17, destLen);

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
                _fileWatcher.Dispose();
                _fileWatcher = null;
            }

            #endregion
        }

        /// <summary>
        /// manages news.txt
        /// </summary>
        private NewsManager _newsManager;

        #region IModule Members

        public Type[] InterfaceDependencies
        {
            get
            {
                return new Type[] {
                    typeof(IPlayerData), 
                    typeof(INetwork), 
                    typeof(ILogManager), 
                    typeof(IConfigManager), 
                    typeof(IServerTimer), 
                    typeof(IArenaManagerCore), 
                    typeof(IMapData), 
                };
            }
        }

        public bool Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;
            _playerData = interfaceDependencies[typeof(IPlayerData)] as IPlayerData;
            _net = interfaceDependencies[typeof(INetwork)] as INetwork;
            _logManager = interfaceDependencies[typeof(ILogManager)] as ILogManager;
            _configManager = interfaceDependencies[typeof(IConfigManager)] as IConfigManager;
            _mainLoop = interfaceDependencies[typeof(IServerTimer)] as IServerTimer;
            _arenaManager = interfaceDependencies[typeof(IArenaManagerCore)] as IArenaManagerCore;
            _mapData = interfaceDependencies[typeof(IMapData)] as IMapData;

            _dlKey = _arenaManager.AllocateArenaData<LinkedList<MapDownloadData>>();

            _net.AddPacket((int)Packets.C2SPacketType.UpdateRequest, packetUpdateRequest);
            _net.AddPacket((int)Packets.C2SPacketType.MapRequest, packetMapNewsRequest);
            _net.AddPacket((int)Packets.C2SPacketType.NewsRequest, packetMapNewsRequest);

            ArenaActionCallback.Register(_mm, arenaAction);

            string newsFilename = _configManager.GetStr(_configManager.Global, "General", "NewsFile");
            if (string.IsNullOrEmpty(newsFilename))
                newsFilename = "news.txt";

            _newsManager = new NewsManager(newsFilename);

            mm.RegisterInterface<IMapNewsDownload>(this);
            return true;
        }

        public bool Unload(ModuleManager mm)
        {
            mm.UnregisterInterface<IMapNewsDownload>();

            _newsManager.Dispose();

            _net.RemovePacket((int)Packets.C2SPacketType.UpdateRequest, packetUpdateRequest);
            _net.RemovePacket((int)Packets.C2SPacketType.MapRequest, packetMapNewsRequest);
            _net.RemovePacket((int)Packets.C2SPacketType.NewsRequest, packetMapNewsRequest);

            ArenaActionCallback.Unregister(_mm, arenaAction);

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

            LinkedList<MapDownloadData> dls = arena[_dlKey] as LinkedList<MapDownloadData>;
            if (dls == null)
                return;

            if (dls.Count == 0)
            {
                _logManager.Log(LogLevel.Warn, "MapNewsDownload", arena, "missing map data");
                return;
            }

            using (DataBuffer buffer = Pool<DataBuffer>.Default.Get())
            {
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
        }

        public uint GetNewsChecksum()
        {
            byte[] data;
            uint checksum;
            if (_newsManager.TryGetNews(out data, out checksum))
                return checksum;

            return 0;
        }

        #endregion

        private void arenaAction(Arena arena, ArenaAction action)
        {
            if (action == ArenaAction.Create)
            {
                // note: asss does this in reverse order, but i think it is a race condition
                _arenaManager.HoldArena(arena);
                _mainLoop.RunInThread<Arena>(arenaActionWork, arena);
            }
            else if (action == ArenaAction.Destroy)
            {
                LinkedList<MapDownloadData> dls = arena[_dlKey] as LinkedList<MapDownloadData>;
                if (dls == null)
                    return;

                dls.Clear();
            }
        }

        private void arenaActionWork(Arena arena)
        {
            if (arena == null)
                return;

            try
            {
                LinkedList<MapDownloadData> dls = arena[_dlKey] as LinkedList<MapDownloadData>;
                if (dls == null)
                    return;

                MapDownloadData data = null;

                string filename = _mapData.GetMapFilename(arena, null);
                if (!string.IsNullOrEmpty(filename))
                {
                    data = compressMap(filename, true);
                }

                if (data == null)
                {
                    _logManager.LogA(LogLevel.Warn, "MapNewsDownload", arena, "can't load level file, falling back to tinymap.lvl");
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
                    if (string.IsNullOrEmpty(lvzInfo.Filename))
                        continue;

                    data = compressMap(lvzInfo.Filename, false);
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

        private MapDownloadData compressMap(string filename, bool docomp)
        {
            try
            {
                MapDownloadData mdd = new MapDownloadData();

                string mapname = Path.GetFileName(filename);
                if (mapname.Length > 20)
                    mapname = mapname.Substring(0, 20);

                mdd.filename = mapname;

                using (FileStream inputStream = new FileStream(filename, FileMode.Open, FileAccess.Read))
                {
                    CRC32 crc32 = new CRC32();

                    mdd.checksum = crc32.GetCrc32(inputStream);
                    mdd.uncmplen = (uint)inputStream.Length;
                    inputStream.Position = 0;

                    int csize = (docomp) ?
                        (int)(1.0011 * inputStream.Length + 35) :
                        (int)inputStream.Length + 17;

                    mdd.cmpmap = new byte[csize];

                    // set up the packet header
                    mdd.cmpmap[0] = (byte)S2CPacketType.MapData;
                    Encoding.ASCII.GetBytes(mapname, 0, (mapname.Length <= 16) ? mapname.Length : 16, mdd.cmpmap, 1);

                    if (docomp)
                    {
                        // using the zlib dll directly via pinvoke
                        byte[] test = new byte[csize];
                        byte[] src = File.ReadAllBytes(filename);
                        uint testsize = (uint)csize;
                        try
                        {
                            int ret = ZlibNativeMethods.Compress(test, ref testsize, src, (uint)src.Length);
                        }
                        catch (Exception ex)
                        {
                        }
                        csize = (int)testsize + 17;
                        mdd.cmpmap = new byte[csize];
                        mdd.cmpmap[0] = (byte)S2CPacketType.MapData;
                        Encoding.ASCII.GetBytes(mapname, 0, (mapname.Length <= 16) ? mapname.Length : 16, mdd.cmpmap, 1);
                        Array.Copy(test, 0, mdd.cmpmap, 17, csize-17);
                        
                        
                        /*
                        // seems to compress, but i'm unable to figure out the resulting size...
                        using (MemoryStream outputStream = new MemoryStream(mdd.cmpmap, 17, mdd.cmpmap.Length - 17))
                        {
                            //using (MemoryStream outputStream = new MemoryStream())
                            using (zlib.ZOutputStream outZStream = new zlib.ZOutputStream(outputStream, zlib.zlibConst.Z_DEFAULT_COMPRESSION))
                            {
                                byte[] buffer = new byte[4096];
                                int len;
                                while ((len = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    outZStream.Write(buffer, 0, len);
                                }

                                outZStream.Flush();
                                //outZStream.Length; // always 0
                                //csize = (int)outZStream.TotalOut + 17;
                                //csize = (int)outputStream.Length + 17;   
                                //outputStream.ToArray()
                            }
                        }
                        */
                    }
                    else
                    {
                        using (MemoryStream outputStream = new MemoryStream(mdd.cmpmap, 17, mdd.cmpmap.Length - 17))
                        {
                            byte[] buffer = new byte[4096];
                            int len;
                            while ((len = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                outputStream.Write(buffer, 0, len);
                            }
                        }
                    }

                    mdd.cmplen = (uint)csize;

                    if (csize > 256 * 1024)
                        _logManager.Log(LogLevel.Warn, "<MapNewsDownload> compressed map/lvz is bigger than 256k: {0}", filename);

                    return mdd;
                }
            }
            catch
            {
                return null;
            }
        }

        private void packetUpdateRequest(Player p, byte[] pkt, int len)
        {
            if (p == null)
                return;

            if (pkt == null)
                return;

            if (len != 1)
            {
                _logManager.LogP(LogLevel.Malicious, "MapNewsDownload", p, "bad update req packet len={0}", len);
                return;
            }

            _logManager.LogP(LogLevel.Drivel, "MapNewsDownload", p, "UpdateRequest");

            // TODO: implement file transfer module and use it here
            IFileTransfer fileTransfer = _mm.GetInterface<IFileTransfer>();
            if (fileTransfer != null)
            {
                try
                {
                    if (!fileTransfer.SendFile(p, "clients/update.exe", string.Empty, false))
                    {
                        _logManager.Log(LogLevel.Warn, "<MapNewsDownload> update request, but error setting up to be sent");
                    }
                }
                finally
                {
                    _mm.ReleaseInterface<IFileTransfer>();
                }
            }

        }

        private void packetMapNewsRequest(Player p, byte[] pkt, int len)
        {
            if (p == null)
                return;

            if (pkt == null)
                return;

            if (pkt[0] == (byte)C2SPacketType.MapRequest)
            {
                if (len != 1 && len != 3)
                {
                    _logManager.LogP(LogLevel.Malicious, "MapNewsDownload", p, "bad map/LVZ req packet len={0}", len);
                    return;
                }

                Arena arena = p.Arena;
                if (arena == null)
                {
                    _logManager.LogP(LogLevel.Malicious, "MapNewsDownload", p, "map request before entering arena");
                    return;
                }

                ushort lvznum = (len == 3) ? (ushort)(pkt[1] | pkt[2] << 8) : (ushort)0;
                bool wantOpt = p.Flags.WantAllLvz;

                MapDownloadData mdd = getMap(arena, lvznum, wantOpt);

                if (mdd == null)
                {
                    _logManager.LogP(LogLevel.Warn, "MapNewsDownload", p, "can't find lvl/lvz {0}", lvznum);
                    return;
                }

                DataLocator dl = new DataLocator(arena, lvznum, wantOpt, mdd.cmplen);

                _net.SendSized<DataLocator>(p, dl, (int)mdd.cmplen, getData);

                _logManager.LogP(LogLevel.Drivel, "MapNewsDownload", p, "sending map/lvz {0} ({1} bytes) (transfer {2})", lvznum, mdd.cmplen, dl);

                // if we're getting these requests, it's too late to set their ship
                // and team directly, we need to go through the in-game procedures
                if (p.IsStandard && (p.Ship != ShipType.Spec || p.Freq != arena.SpecFreq))
                {
                    IGame game = _mm.GetInterface<IGame>();
                    if (game != null)
                    {
                        try
                        {
                            game.SetFreqAndShip(p, ShipType.Spec, arena.SpecFreq);
                        }
                        finally
                        {
                            _mm.ReleaseInterface<IGame>();
                        }
                    }
                }
            }
            else if (pkt[0] == (byte)C2SPacketType.NewsRequest)
            {
                if (len != 1)
                {
                    _logManager.LogP(LogLevel.Malicious, "MapNewsDownload", p, "bad news req packet len={0}", len);
                    return;
                }

                byte[] compressedNewsData;
                uint checksum;
                if (_newsManager.TryGetNews(out compressedNewsData, out checksum))
                {
                    _net.SendSized<byte[]>(p, compressedNewsData, compressedNewsData.Length, getNews);
                    _logManager.LogP(LogLevel.Drivel, "MapNewsDownload", p, "sending news.txt ({0} bytes)", compressedNewsData.Length);
                }
                else
                {
                    _logManager.Log(LogLevel.Warn, "<MapNewsDownload> news request, but compressed news doesn't exist");
                }
            }
        }

        private MapDownloadData getMap(Arena arena, int lvznum, bool wantOpt)
        {
            LinkedList<MapDownloadData> dls = arena[_dlKey] as LinkedList<MapDownloadData>;
            if (dls == null)
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

        private void getNews(byte[] newsData, int offset, byte[] buf, int bufStartIndex, int bytesNeeded)
        {
            if (bytesNeeded == 0)
            {
                _logManager.Log(LogLevel.Drivel, "<mapnewsdl> finished news download");
                return;
            }

            if (newsData != null)
            {
                Array.Copy(newsData, offset, buf, bufStartIndex, bytesNeeded);
                return;
            }

            // getting to here is bad...
            for (int x = 0; x < bytesNeeded; x++)
            {
                buf[offset + x] = 0;
            }
        }

        private void getData(DataLocator dl, int offset, byte[] buf, int bufStartIndex, int bytesNeeded)
        {
            if (bytesNeeded == 0)
            {
                _logManager.Log(LogLevel.Drivel, "<mapnewsdl> finished map/lvz download (transfer {0})", dl);
                return;
            }

            if(dl.Arena != null)
            {
                // map or lvz
                MapDownloadData mdd = getMap(dl.Arena, dl.LvzNum, dl.WantOpt);
                if (mdd != null || dl.Len == mdd.cmplen)
                {
                    Array.Copy(mdd.cmpmap, offset, buf, bufStartIndex, bytesNeeded);
                    return;
                }
            }

            // getting to here is bad...
            for (int x = 0; x < bytesNeeded; x++)
            {
                buf[offset + x] = 0;
            }
        }
    }
}

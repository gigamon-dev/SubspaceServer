using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using SS.Core.Packets;
using System.Diagnostics;
using System.Threading;
using System.IO;

namespace SS.Core
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

        private MapDownloadData _newsDownloadData = null;
        private ReaderWriterLock _newsLock = new ReaderWriterLock();

        private byte[] _emergencyMap = new byte[]
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
                    //typeof(IMapData), 
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
            //_mapData = interfaceDependencies[typeof(IMapData)] as IMapData;

            _dlKey = _arenaManager.AllocateArenaData<LinkedList<MapDownloadData>>();

            _net.AddPacket((int)Packets.C2SPacketType.UpdateRequest, packetUpdateRequest);
            _net.AddPacket((int)Packets.C2SPacketType.MapRequest, packetMapNewsRequest);
            _net.AddPacket((int)Packets.C2SPacketType.NewsRequest, packetMapNewsRequest);

            mm.RegisterCallback<ArenaActionEventHandler>(Constants.Events.ArenaAction, new ArenaActionEventHandler(arenaAction));

            mm.RegisterInterface<IMapNewsDownload>(this);
            return true;
        }

        public bool Unload(ModuleManager mm)
        {
            mm.UnregisterInterface<IMapNewsDownload>();

            _net.RemovePacket((int)Packets.C2SPacketType.UpdateRequest, packetUpdateRequest);
            _net.RemovePacket((int)Packets.C2SPacketType.MapRequest, packetMapNewsRequest);
            _net.RemovePacket((int)Packets.C2SPacketType.NewsRequest, packetMapNewsRequest);

            mm.UnregisterCallback(Constants.Events.ArenaAction, new ArenaActionEventHandler(arenaAction));
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

                // TODO: get data from _mapData
                //if(_mapData.GetMapFilename(arena, out filename, null))
                string filename = @"maps\smallmap.lvl";
                data = compressMap(filename, true);

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
                //_mapData.EnumLVZFiles(arena, 
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
                        csize = (int)(1.0011 * inputStream.Length + 35) :
                        csize = (int)inputStream.Length + 17;

                    mdd.cmpmap = new byte[csize];

                    // set up the packet header
                    mdd.cmpmap[0] = (byte)S2CPacketType.MapData;
                    Encoding.ASCII.GetBytes(mapname, 0, (mapname.Length <= 16) ? mapname.Length : 16, mdd.cmpmap, 1);

                    if (docomp)
                    {
                        //using (MemoryStream outputStream = new MemoryStream(mdd.cmpmap, 17, mdd.cmpmap.Length - 17))
                        using (MemoryStream outputStream = new MemoryStream())
                        using (zlib.ZOutputStream outZStream = new zlib.ZOutputStream(outputStream, zlib.zlibConst.Z_DEFAULT_COMPRESSION))
                        {
                            byte[] buffer = new byte[4096];
                            int len;
                            while ((len = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                outZStream.Write(buffer, 0, len);
                            }

                            outZStream.Flush();
                            //csize = (int)outZStream.TotalOut;
                        }
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
        }

        private void packetMapNewsRequest(Player p, byte[] pkt, int len)
        {
            if (p == null)
                return;

            if (pkt == null)
                return;

            bool wantOpt = p.Flags.WantAllLvz;
            
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
                    //IGame game = _mm.GetInterface<IGame>();
                    //if(game != null)
                    //{
                    //  game.SetFreqAndShip(p, ShipType.Spec, arena.SpecFreq);
                    //  _mm.ReleaseInterface<IGame>();
                    //}
                }
            }
            else if (pkt[0] == (byte)C2SPacketType.NewsRequest)
            {
                if (len != 1)
                {
                    _logManager.LogP(LogLevel.Malicious, "MapNewsDownload", p, "bad news req packet len={0}", len);
                    return;
                }

                // TODO: 
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

        private void getData(DataLocator dl, int offset, byte[] buf, int bufStartIndex, int bytesNeeded)
        {
            if (bytesNeeded == 0)
            {
                _logManager.Log(LogLevel.Drivel, "<mapnewsdl> finished map/news download (transfer {0})", dl);
                return;
            }
            
            if(dl.Arena == null)
            {
                // news
                _newsLock.AcquireReaderLock(Timeout.Infinite);

                try
                {
                    if ((_newsDownloadData != null) && (dl.Len == _newsDownloadData.cmplen))
                    {
                        Array.Copy(_newsDownloadData.cmpmap, offset, buf, bufStartIndex, bytesNeeded);
                        return;
                    }
                }
                finally
                {
                    _newsLock.ReleaseReaderLock();
                }
            }
            else
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using SS.Core.Packets;
using System.Diagnostics;

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

        private byte[] _emergencyMap = new byte[]
        {
            0x2a, 0x74, 0x69, 0x6e, 0x79, 0x6d, 0x61, 0x70,
            0x2e, 0x6c, 0x76, 0x6c, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x78, 0x9c, 0x63, 0x60, 0x60, 0x60, 0x04,
            0x00, 0x00, 0x05, 0x00, 0x02
        };

        private class MapDownloadData
        {
            public uint checksum;
            public uint uncmplen;
            public uint cmplen;
            public bool optional;
            public byte[] cmpmap;
            public string filename;
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

            mm.RegisterCallback<ArenaActionEventHandler>(Constants.Events.ArenaAction, new ArenaActionEventHandler(arenaAction));

            mm.RegisterInterface<IMapNewsDownload>(this);
            return true;
        }

        public bool Unload(ModuleManager mm)
        {
            mm.UnregisterInterface<IMapNewsDownload>();
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
    }
}

using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class MapData : IModule, IMapData
    {
        private ComponentBroker _broker;
        private IMainloop _mainloop;
        private IConfigManager _configManager;
        private IArenaManager _arenaManager;
        private ILogManager _logManager;
        private InterfaceRegistrationToken _iMapDataToken;

        private int _lvlKey;

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IMainloop mainloop,
            IConfigManager configManager,
            IArenaManager arenaManager,
            ILogManager logManager)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(broker));

            _lvlKey = _arenaManager.AllocateArenaData<ExtendedLvl>();
            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            _iMapDataToken = _broker.RegisterInterface<IMapData>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<IMapData>(ref _iMapDataToken) != 0)
                return false;

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
            _arenaManager.FreeArenaData(_lvlKey);

            return true;
        }

        #endregion

        #region IMapData Members

        string IMapData.GetMapFilename(Arena arena, string mapname)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            return GetMapFilename(arena, mapname);
        }

        [ConfigHelp("General", "Map", ConfigScope.Arena, typeof(string), Description = "The name of the level file for the arena.")]
        private string GetMapFilename(Arena arena, string mapname)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            Dictionary<char, string> repls = new Dictionary<char, string>()
            {
                {'b', arena.BaseName}, 
                {'m', null}
            };

            if (string.IsNullOrEmpty(mapname))
                mapname = _configManager.GetStr(arena.Cfg, "General", "Map");

            bool isLvl = false;

            if (string.IsNullOrEmpty(mapname) == false)
            {
                repls['m'] = mapname;

                if (string.Equals(Path.GetExtension(mapname), ".lvl", StringComparison.OrdinalIgnoreCase))
                    isLvl = true;
            }

            if (PathUtil.find_file_on_path(
                out string filename,
                isLvl ? Constants.CFG_LVL_SEARCH_PATH : Constants.CFG_LVZ_SEARCH_PATH,
                repls) == 0)
            {
                return filename;
            }

            return null;
        }

        [ConfigHelp("General", "LevelFiles", ConfigScope.Arena, typeof(string), 
            "The list of lvz files for the arena. LevelFiles1 through LevelFiles15 are also supported.")]
        IEnumerable<LvzFileInfo> IMapData.LvzFilenames(Arena arena)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            int count = 0;

            for (int x = 0; x <= 15; x++)
            {
                string lvz;

                if (x == 0)
                {
                    lvz = _configManager.GetStr(arena.Cfg, "General", "LevelFiles");

                    if (string.IsNullOrWhiteSpace(lvz))
                        lvz = _configManager.GetStr(arena.Cfg, "Misc", "LevelFiles");
                }
                else
                {
                    lvz = _configManager.GetStr(arena.Cfg, "General", "LevelFiles" + x);
                }

                if (string.IsNullOrWhiteSpace(lvz))
                    continue;

                string[] lvzNameArray = lvz.Split(",: ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                foreach (string lvzName in lvzNameArray)
                {
                    string real = lvzName[0] == '+' ? lvzName.Substring(1) : lvzName;
                    string fname = GetMapFilename(arena, real);
                    if (string.IsNullOrWhiteSpace(fname))
                        continue;

                    yield return new LvzFileInfo(fname, (lvzName[0] == '+'));

                    if (++count >= Constants.MaxLvzFiles)
                        yield break;
                }
            }
        }

        string IMapData.GetAttribute(Arena arena, string key)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!(arena[_lvlKey] is ExtendedLvl lvl))
                throw new Exception("missing lvl data");

            if (lvl.TryGetAttribute(key, out string attributeValue))
                return attributeValue;
            else
                return null;
        }

        IEnumerable<ArraySegment<byte>> IMapData.ChunkData(Arena arena, uint chunkType)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!(arena[_lvlKey] is ExtendedLvl lvl))
                throw new Exception("missing lvl data");

            return lvl.ChunkData(chunkType);
        }

        int IMapData.GetFlagCount(Arena arena)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!(arena[_lvlKey] is ExtendedLvl lvl))
                throw new Exception("missing lvl data");

            return lvl.FlagCount;
        }

        MapTile? IMapData.GetTile(Arena arena, MapCoordinate coord)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!(arena[_lvlKey] is ExtendedLvl lvl))
                throw new Exception("missing lvl data");

            if (lvl.TryGetTile(coord, out MapTile tile))
                return tile;
            else
                return null;
        }
/*
        private struct FindEmptyTileContext
        {
            public enum Direction { up, right, down, left };
            public Direction dir;
            public int upto, remaining;
            public int x, y;
        }
*/
        bool IMapData.FindEmptyTileNear(Arena arena, ref short x, ref short y)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!(arena[_lvlKey] is ExtendedLvl lvl))
                throw new Exception("missing lvl data");

            if (!lvl.TryGetTile(new MapCoordinate(x, y), out MapTile tile))
                return true;

            // TODO

            return false;
        }

        bool IMapData.FindEmptyTileInRegion(Arena arena, MapRegion region)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (region == null)
                throw new ArgumentNullException(nameof(region));

            if (!(arena[_lvlKey] is ExtendedLvl lvl))
                throw new Exception("missing lvl data");

            // TODO

            return false;
        }

        uint IMapData.GetChecksum(Arena arena, uint key)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!(arena[_lvlKey] is ExtendedLvl lvl))
                throw new Exception("missing lvl data");

            int saveKey = (int)key;

            lvl.Lock();

            try
            {
                // this is the same way asss 1.4.4 calculates the checksum
                for (short y = (short)(saveKey % 32); y < 1024; y += 32)
                {
                    for (short x = (short)(saveKey % 31); x < 1024; x += 31)
                    {
                        lvl.TryGetTile(new MapCoordinate(x, y), out MapTile tile); // if no tile, it will be zero'd out which is what we want
                        if ((tile >= MapTile.TileStart && tile <= MapTile.TileEnd) || tile.IsSafe)
                            key += (uint)(saveKey ^ (byte)tile);
                    }
                }
            }
            finally
            {
                lvl.Unlock();
            }

            return key;
        }

        int IMapData.GetRegionCount(Arena arena)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!(arena[_lvlKey] is ExtendedLvl lvl))
                throw new Exception("missing lvl data");

            return lvl.RegionCount;
        }

        MapRegion IMapData.FindRegionByName(Arena arena, string name)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!(arena[_lvlKey] is ExtendedLvl lvl))
                throw new Exception("missing lvl data");

            return lvl.FindRegionByName(name);
        }

        IImmutableSet<MapRegion> IMapData.RegionsAt(Arena arena, MapCoordinate coord)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!(arena[_lvlKey] is ExtendedLvl lvl))
                throw new Exception("missing lvl data");

            return lvl.RegionsAtCoord(coord.X, coord.Y);
        }

        IImmutableSet<MapRegion> IMapData.RegionsAt(Arena arena, short x, short y)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!(arena[_lvlKey] is ExtendedLvl lvl))
                throw new Exception("missing lvl data");

            return lvl.RegionsAtCoord(x, y);
        }

        #endregion

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null)
                return;

            if (action == ArenaAction.Create || action == ArenaAction.Destroy)
            {
                _arenaManager.HoldArena(arena); // the worker thread will unhold
                _mainloop.QueueThreadPoolWorkItem(ArenaActionWork, arena);
            }
        }

        private void ArenaActionWork(Arena arena)
        {
            if (arena == null)
                return;

            try
            {
                if (!(arena[_lvlKey] is ExtendedLvl lvl))
                    return;

                lvl.Lock();

                try
                {
                    // clear on either create or destroy
                    lvl.ClearLevel();

                    // on create, do work
                    if (arena.Status < ArenaState.Running)
                    {
                        string mapname = GetMapFilename(arena, null);

                        if (!string.IsNullOrEmpty(mapname) &&
                            lvl.LoadFromFile(mapname))
                        {
                            _logManager.LogA(LogLevel.Info, nameof(MapData), arena, "successfully processed map file '{0}' with {1} tiles, {2} flags, {3} regions, {4} errors", mapname, lvl.TileCount, lvl.FlagCount, lvl.RegionCount, lvl.ErrorCount);

                            /*
                            // useful check to see that we are in fact loading correctly
                            using (Bitmap bmp = lvl.ToBitmap())
                            {
                                bmp.Save(
                                    Path.ChangeExtension(
                                        Path.GetFileNameWithoutExtension(mapname),
                                        ".bmp"));
                            }
                            */
                        }
                        else
                        {
                            _logManager.LogA(LogLevel.Warn, nameof(MapData), arena, "error finding or reading map file '{0}'", mapname);

                            // fall back to emergency. this matches the compressed map in MapNewsDownload.cs
                            lvl.SetAsEmergencyMap();
                        }
                    }
                }
                finally
                {
                    lvl.Unlock();
                }
            }
            finally
            {
                _arenaManager.UnholdArena(arena);
            }
        }
    }
}

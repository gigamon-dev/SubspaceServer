using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Utilities;
using System.Drawing;

namespace SS.Core.Modules
{
    public class MapData : IModule, IMapData
    {
        private ModuleManager _mm;
        private IServerTimer _mainLoop;
        private IConfigManager _configManager;
        private IArenaManagerCore _arenaManager;
        private ILogManager _logManager;

        private int _lvlKey;

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get
            {
                return new Type[]
                {
                    typeof(IServerTimer), 
                    typeof(IConfigManager), 
                    typeof(IArenaManagerCore), 
                    typeof(ILogManager), 
                };
            }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;
            _mainLoop = interfaceDependencies[typeof(IServerTimer)] as IServerTimer;
            _configManager = interfaceDependencies[typeof(IConfigManager)] as IConfigManager;
            _arenaManager = interfaceDependencies[typeof(IArenaManagerCore)] as IArenaManagerCore;
            _logManager = interfaceDependencies[typeof(ILogManager)] as ILogManager;

            _lvlKey = _arenaManager.AllocateArenaData<ExtendedLvl>();
            ArenaActionCallback.Register(_mm, arenaAction);
            _mm.RegisterInterface<IMapData>(this);

            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            _mm.UnregisterInterface<IMapData>();
            ArenaActionCallback.Unregister(_mm, arenaAction);
            _arenaManager.FreeArenaData(_lvlKey);

            return true;
        }

        #endregion

        #region IMapData Members

        string IMapData.GetMapFilename(Arena arena, string mapname)
        {
            return getMapFilename(arena, mapname);
        }

        private string getMapFilename(Arena arena, string mapname)
        {
            if (arena == null)
                return null;

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

                if (string.Compare(Path.GetExtension(mapname), ".lvl", true) == 0)
                    isLvl = true;
            }

            string filename;
            if (PathUtil.find_file_on_path(
                out filename,
                isLvl ? Constants.CFG_LVL_SEARCH_PATH : Constants.CFG_LVZ_SEARCH_PATH,
                repls) == 0)
            {
                return filename;
            }

            return null;
        }

        string IMapData.GetAttribute(Arena arena, string key)
        {
            if (arena == null)
                throw new ArgumentNullException("arena");

            ExtendedLvl lvl = arena[_lvlKey] as ExtendedLvl;
            if (lvl == null)
                throw new Exception("missing lvl data");

            string attributeValue;
            if (lvl.TryGetAttribute(key, out attributeValue))
                return attributeValue;
            else
                return null;
        }

        int IMapData.GetFlagCount(Arena arena)
        {
            if (arena == null)
                throw new ArgumentNullException("arena");

            ExtendedLvl lvl = arena[_lvlKey] as ExtendedLvl;
            if (lvl == null)
                throw new Exception("missing lvl data");

            return lvl.FlagCount;
        }

        MapTile? IMapData.GetTile(Arena arena, MapCoordinate coord)
        {
            if (arena == null)
                throw new ArgumentNullException("arena");

            ExtendedLvl lvl = arena[_lvlKey] as ExtendedLvl;
            if (lvl == null)
                throw new Exception("missing lvl data");

            MapTile tile;
            if (lvl.TryGetTile(coord, out tile))
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
                throw new ArgumentNullException("arena");

            ExtendedLvl lvl = arena[_lvlKey] as ExtendedLvl;
            if (lvl == null)
                throw new Exception("missing lvl data");

            MapTile tile;
            if (!lvl.TryGetTile(new MapCoordinate(x, y), out tile))
                return true;

            // TODO

            return false;
        }

        uint IMapData.GetChecksum(Arena arena, uint key)
        {
            if (arena == null)
                throw new ArgumentNullException("arena");

            ExtendedLvl lvl = arena[_lvlKey] as ExtendedLvl;
            if (lvl == null)
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
                        MapTile tile;
                        lvl.TryGetTile(new MapCoordinate(x, y), out tile); // if no tile, it will be zero'd out which is what we want
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
                throw new ArgumentNullException("arena");

            ExtendedLvl lvl = arena[_lvlKey] as ExtendedLvl;
            if (lvl == null)
                throw new Exception("missing lvl data");

            return lvl.RegionCount;
        }

        MapRegion IMapData.FindRegionByName(Arena arena, string name)
        {
            if (arena == null)
                throw new ArgumentNullException("arena");

            ExtendedLvl lvl = arena[_lvlKey] as ExtendedLvl;
            if (lvl == null)
                throw new Exception("missing lvl data");

            return lvl.FindRegionByName(name);
        }

        IEnumerable<MapRegion> IMapData.RegionsAt(Arena arena, MapCoordinate coord)
        {
            if (arena == null)
                throw new ArgumentNullException("arena");

            ExtendedLvl lvl = arena[_lvlKey] as ExtendedLvl;
            if (lvl == null)
                throw new Exception("missing lvl data");

            return lvl.RegionsAtCoord(coord.X, coord.Y);
        }

        IEnumerable<MapRegion> IMapData.RegionsAt(Arena arena, short x, short y)
        {
            if (arena == null)
                throw new ArgumentNullException("arena");

            ExtendedLvl lvl = arena[_lvlKey] as ExtendedLvl;
            if (lvl == null)
                throw new Exception("missing lvl data");

            return lvl.RegionsAtCoord(x, y);
        }

        #endregion

        private void arenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null)
                return;

            if (action == ArenaAction.Create || action == ArenaAction.Destroy)
            {
                _arenaManager.HoldArena(arena); // the worker thread will unhold
                _mainLoop.RunInThread<Arena>(arenaActionWork, arena);
            }
        }

        private void arenaActionWork(Arena arena)
        {
            if (arena == null)
                return;

            try
            {
                ExtendedLvl lvl = arena[_lvlKey] as ExtendedLvl;
                if (lvl == null)
                    return;

                lvl.Lock();

                try
                {
                    // clear on either create or destroy
                    lvl.ClearLevel();

                    // on create, do work
                    if (arena.Status < ArenaState.Running)
                    {
                        string mapname = getMapFilename(arena, null);

                        if (!string.IsNullOrEmpty(mapname) &&
                            lvl.LoadFromFile(mapname))
                        {
                            _logManager.LogA(LogLevel.Info, "MapData", arena, "successfully processed map file '{0}' with {1} tiles, {2} flags, {3} regions, {4} errors", mapname, lvl.TileCount, lvl.FlagCount, lvl.RegionCount, lvl.ErrorCount);

                            /*
                            // useful check to see that we are in fact loading correctly
                            using (Bitmap bmp = lvl.ToBitmap())
                            {
                                bmp.Save("C:\\mapimage.bmp");
                            }
                            */
                        }
                        else
                        {
                            _logManager.LogA(LogLevel.Warn, "MapData", arena, "error finding or reading map file '{0}'", mapname);

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

using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that manages map data (lvl and lvz files) for arenas.
    /// </summary>
    [CoreModuleInfo]
    public class MapData : IModule, IMapData
    {
        private ComponentBroker _broker;
        private IMainloop _mainloop;
        private IConfigManager _configManager;
        private IArenaManager _arenaManager;
        private ILogManager _logManager;
        private InterfaceRegistrationToken<IMapData> _iMapDataToken;

        private ArenaDataKey<ArenaData> _adKey;

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

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            _iMapDataToken = _broker.RegisterInterface<IMapData>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iMapDataToken) != 0)
                return false;

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
            _arenaManager.FreeArenaData(ref _adKey);

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
        private string GetMapFilename(Arena arena, string mapName)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (string.IsNullOrWhiteSpace(mapName))
            {
                mapName = _configManager.GetStr(arena.Cfg, "General", "Map");
            }

            bool isLvl = !string.IsNullOrWhiteSpace(mapName)
                && string.Equals(Path.GetExtension(mapName), ".lvl", StringComparison.OrdinalIgnoreCase);

            return PathUtil.FindFileOnPath(
                isLvl ? Constants.LvlSearchPaths : Constants.LvzSearchPaths,
                mapName, 
                arena.BaseName);
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
                    string real = lvzName[0] == '+' ? lvzName[1..] : lvzName;
                    string fname = GetMapFilename(arena, real);
                    if (string.IsNullOrWhiteSpace(fname))
                        continue;

                    yield return new LvzFileInfo(fname, (lvzName[0] == '+'));

                    if (++count >= S2C_MapFilename.MaxLvzFiles)
                        yield break;
                }
            }
        }

        string IMapData.GetAttribute(Arena arena, string key)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                throw new Exception("missing lvl data");

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl.TryGetAttribute(key, out string attributeValue))
                    return attributeValue;
                else
                    return null;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        IEnumerable<ReadOnlyMemory<byte>> IMapData.ChunkData(Arena arena, uint chunkType)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                throw new Exception("missing lvl data");

            ad.Lock.EnterReadLock();

            try
            {
                return ad.Lvl.ChunkData(chunkType);
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        int IMapData.GetTileCount(Arena arena)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                throw new Exception("missing lvl data");

            ad.Lock.EnterReadLock();

            try
            {
                return ad.Lvl.TileCount;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        int IMapData.GetFlagCount(Arena arena)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                throw new Exception("missing lvl data");

            ad.Lock.EnterReadLock();

            try
            {
                return ad.Lvl.FlagCount;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        IReadOnlyList<string> IMapData.GetErrors(Arena arena)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                throw new Exception("missing lvl data");

            ad.Lock.EnterReadLock();

            try
            {
                return ad.Lvl.Errors;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        MapTile? IMapData.GetTile(Arena arena, MapCoordinate coord)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                throw new Exception("missing lvl data");

            ad.Lock.EnterReadLock();

            try
            {
                return ad.Lvl.TryGetTile(coord, out MapTile tile) ? tile : null;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        bool IMapData.TryGetFlagCoordinate(Arena arena, short flagId, out MapCoordinate coordinate)
        {
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData ad))
            {
                coordinate = default;
                return false;
            }

            ad.Lock.EnterReadLock();

            try
            {
                return ad.Lvl.TryGetFlagCoordinate(flagId, out coordinate);
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        private enum Direction { Up, Right, Down, Left };

        private struct FindEmptyTileContext
        {
            public Direction Dir;
            public int UpTo, Remaining;
            public short X, Y;
        }

        bool IMapData.TryFindEmptyTileNear(Arena arena, ref short x, ref short y)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                throw new Exception("missing lvl data");

            ad.Lock.EnterReadLock();

            try
            {
                // Look for an empty tile, from the staring coordinate and spiral around it (starting at the top, going clockwise).

                FindEmptyTileContext context = new()
                {
                    Dir = Direction.Left,
                    UpTo = 0,
                    Remaining = 1,
                    X = (short)(x + 1),
                    Y = y,
                };

                while (true)
                {
                    // move 1 in current direction
                    switch (context.Dir)
                    {
                        case Direction.Down: context.Y++; break;
                        case Direction.Right: context.X++; break;
                        case Direction.Up: context.Y--; break;
                        case Direction.Left: context.X--; break;
                    }

                    context.Remaining--;

                    // if we are at the end of the line
                    if (context.Remaining == 0)
                    {
                        context.Dir = (Direction)(((int)context.Dir + 1) % 4);
                        if (context.Dir == Direction.Up || context.Dir == Direction.Up)
                            context.UpTo++;

                        context.Remaining = context.UpTo;
                    }

                    // check if it's a valid coordinate and that it's empty
                    if (context.X < 0 || context.X > 1023 || context.Y < 0 || context.Y > 1023
                        || (ad.Lvl.TryGetTile(new MapCoordinate(context.X, context.Y), out MapTile tile) && tile != MapTile.None))
                    {
                        if (context.UpTo < 35)
                            continue;
                        else
                            return false;
                    }

                    // Found it!
                    x = context.X;
                    y = context.Y;
                    return true;
                }
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        uint IMapData.GetChecksum(Arena arena, uint key)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                throw new Exception("missing lvl data");

            int saveKey = (int)key;

            ad.Lock.EnterReadLock();

            try
            {
                // this is the same way asss 1.4.4 calculates the checksum
                for (short y = (short)(saveKey % 32); y < 1024; y += 32)
                {
                    for (short x = (short)(saveKey % 31); x < 1024; x += 31)
                    {
                        ad.Lvl.TryGetTile(new MapCoordinate(x, y), out MapTile tile); // if no tile, it will be zero'd out which is what we want
                        if ((tile >= MapTile.TileStart && tile <= MapTile.TileEnd) || tile.IsSafe)
                            key += (uint)(saveKey ^ (byte)tile);
                    }
                }
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }

            return key;
        }

        int IMapData.GetRegionCount(Arena arena)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                throw new Exception("missing lvl data");

            ad.Lock.EnterReadLock();

            try
            {
                return ad.Lvl.RegionCount;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        MapRegion IMapData.FindRegionByName(Arena arena, string name)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                throw new Exception("missing lvl data");

            ad.Lock.EnterReadLock();

            try
            {
                return ad.Lvl.FindRegionByName(name);
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        ImmutableHashSet<MapRegion> IMapData.RegionsAt(Arena arena, MapCoordinate coord)
        {
            return ((IMapData)this).RegionsAt(arena, coord.X, coord.Y);
        }

        ImmutableHashSet<MapRegion> IMapData.RegionsAt(Arena arena, short x, short y)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                throw new Exception("missing lvl data");

            ad.Lock.EnterReadLock();

            try
            {
                return ad.Lvl.RegionsAtCoord(x, y);
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        void IMapData.SaveImage(Arena arena, string path)
        {
			ArgumentNullException.ThrowIfNull(arena);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                throw new Exception("missing lvl data");

            ad.Lock.EnterReadLock();

            try
            {
                ad.Lvl.SaveImage(path);
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

		void IMapData.SaveImage(Arena arena, Stream stream, ReadOnlySpan<char> imageFormat)
        {
            ArgumentNullException.ThrowIfNull(arena);
			ArgumentNullException.ThrowIfNull(stream);

			if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
				throw new Exception("missing lvl data");

			ad.Lock.EnterReadLock();

			try
			{
				ad.Lvl.SaveImage(stream, imageFormat);
			}
			finally
			{
				ad.Lock.ExitReadLock();
			}
		}

		#endregion

		private async void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena == null)
                return;

            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
                return;

            if (action == ArenaAction.PreCreate)
            {
                _arenaManager.HoldArena(arena);

                try
                {
                    // load the level asynchronously
                    ExtendedLvl lvl = await LoadMapAsync(arena).ConfigureAwait(false);

                    // Note: The await is purposely not within the lock
                    // since the lock/unlock has to be performed on the same thread.

                    ad.Lock.EnterWriteLock();

                    try
                    {
                        ad.Lvl = lvl;
                    }
                    finally
                    {
                        ad.Lock.ExitWriteLock();
                    }
                }
                finally
                {
                    _arenaManager.UnholdArena(arena);
                }
            }
            else if (action == ArenaAction.Destroy)
            {
                ad.Lock.EnterWriteLock();

                try
                {
                    ad.Lvl = null;
                }
                finally
                {
                    ad.Lock.ExitWriteLock();
                }
            }
        }

        private async Task<ExtendedLvl> LoadMapAsync(Arena arena)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            string path = GetMapFilename(arena, null);
            ExtendedLvl lvl = null;

            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    lvl = await Task.Run(() => new ExtendedLvl(path)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logManager.LogA(LogLevel.Warn, nameof(MapData), arena, $"Error reading map file '{path}'. {ex.Message}");
                }
            }
            else
            {
                _logManager.LogA(LogLevel.Warn, nameof(MapData), arena, "Error finding map filename.");
            }

            if (lvl != null)
            {
                _logManager.LogA(LogLevel.Info, nameof(MapData), arena, $"Successfully processed map file '{path}' with {lvl.TileCount} tiles, {lvl.FlagCount} flags, {lvl.RegionCount} regions, {lvl.Errors.Count} errors");
            }
            else
            {
                // fall back to emergency. this matches the compressed map in MapNewsDownload.cs
                lvl = ExtendedLvl.EmergencyMap;
            }

            return lvl;
        }

        #region Helper types

        private class ArenaData : IResettable
        {
            public ExtendedLvl Lvl = null;
            public readonly ReaderWriterLockSlim Lock = new();

            public bool TryReset()
            {
                Lock.EnterWriteLock();

                try
                {
                    Lvl = null;
                }
                finally
                {
                    Lock.ExitWriteLock();
                }

                return true;
            }
        }

        #endregion
    }
}

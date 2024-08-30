using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Core.Modules.FlagGame;
using SS.Packets.Game;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Hashing;
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
        private readonly IArenaManager _arenaManager;
        private readonly IComponentBroker _broker;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IMainloop _mainloop;
        private readonly IObjectPoolManager _objectPoolManager;
        private InterfaceRegistrationToken<IMapData>? _iMapDataToken;

        private ArenaDataKey<ArenaData> _adKey;

        private const string Error_ArenaDataNotLoaded = $"Arena data not available. In the arena life-cycle, data becomes available after the {nameof(ArenaAction.PreCreate)} step, and is removed on the {nameof(ArenaAction.Destroy)} step.";

        private readonly LvlData _emergencyMapData;
        private readonly Dictionary<LvlDataId, LvlData> _lvlDictionary = new(Constants.TargetArenaCount);
        private readonly object _lock = new();

        private readonly DefaultObjectPool<LvlData> _lvlDataPool = new(new DefaultPooledObjectPolicy<LvlData>(), Constants.TargetArenaCount);

        public MapData(
            IArenaManager arenaManager,
            IComponentBroker broker,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloop mainloop,
            IObjectPoolManager objectPoolManager)
        {
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(broker));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            _emergencyMapData = new LvlData();
            _emergencyMapData.Initialize(default, ExtendedLvl.EmergencyMap);
        }

        #region IModule Members

        bool IModule.Load(IComponentBroker broker)
        {
            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            ArenaActionCallback.Register(_broker, Callback_ArenaAction);
            _iMapDataToken = _broker.RegisterInterface<IMapData>(this);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iMapDataToken) != 0)
                return false;

            ArenaActionCallback.Unregister(_broker, Callback_ArenaAction);
            _arenaManager.FreeArenaData(ref _adKey);

            return true;
        }

        #endregion

        #region IMapData Members

        [ConfigHelp("General", "Map", ConfigScope.Arena, Description = "The name of the level file for the arena.")]
        Task<string?> IMapData.GetMapFilenameAsync(Arena arena, string? mapName)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (string.IsNullOrWhiteSpace(mapName))
            {
                string? filename = _configManager.GetStr(arena.Cfg!, "General", "Map");
                if (string.IsNullOrWhiteSpace(filename))
                    return Task.FromResult((string?)null);

                mapName = filename;
            }

            bool isLvl = !string.IsNullOrWhiteSpace(mapName)
                && string.Equals(Path.GetExtension(mapName), ".lvl", StringComparison.OrdinalIgnoreCase);

            return PathUtil.FindFileOnPathAsync(
                isLvl ? Constants.LvlSearchPaths : Constants.LvzSearchPaths,
                mapName,
                arena.BaseName);
        }

        [ConfigHelp("General", "LevelFiles", ConfigScope.Arena,
            Description = "The list of lvz files for the arena. LevelFiles1 through LevelFiles15 are also supported.")]
        async IAsyncEnumerable<LvzFileInfo> IMapData.LvzFilenamesAsync(Arena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);

            ConfigHandle ch = arena.Cfg!;
            int count = 0;

            for (int x = 0; x < S2C_MapFilename.MaxLvzFiles; x++)
            {
                string? lvz;

                if (x == 0)
                {
                    lvz = _configManager.GetStr(ch, "General", "LevelFiles");

                    if (string.IsNullOrWhiteSpace(lvz))
                        lvz = _configManager.GetStr(ch, "Misc", "LevelFiles");
                }
                else
                {
                    lvz = GetLevelFileSetting(ch, x);
                }

                if (string.IsNullOrWhiteSpace(lvz))
                    continue;

                string[] lvzNameArray = lvz.Split(",: ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                foreach (string lvzName in lvzNameArray)
                {
                    string real = lvzName[0] == '+' ? lvzName[1..] : lvzName;
                    string? fname = await ((IMapData)this).GetMapFilenameAsync(arena, real).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(fname))
                        continue;

                    yield return new LvzFileInfo(fname, (lvzName[0] == '+'));

                    if (++count >= S2C_MapFilename.MaxLvzFiles)
                        yield break;
                }
            }

            string? GetLevelFileSetting(ConfigHandle ch, int number)
            {
                Span<char> key = stackalloc char["LevelFiles".Length + 11];
                if (!key.TryWrite($"LevelFiles{number}", out int charsWritten))
                    return null;

                return _configManager.GetStr(ch, "General", key[..charsWritten]);
            }
        }

        string? IMapData.GetAttribute(Arena arena, string key)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                if (ad.Lvl.TryGetAttribute(key, out string? attributeValue))
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
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                return ad.Lvl.ChunkData(chunkType);
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        int IMapData.GetTileCount(Arena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                return ad.Lvl.TileCount;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        int IMapData.GetFlagCount(Arena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                return ad.Lvl.FlagCount;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        IReadOnlyList<string> IMapData.GetErrors(Arena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                return ad.Lvl.Errors;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        uint IMapData.GetChecksum(Arena arena, uint key)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            int saveKey = (int)key;

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                // this is the same way asss 1.4.4 calculates the checksum
                for (short y = (short)(saveKey % 32); y < 1024; y += 32)
                {
                    for (short x = (short)(saveKey % 31); x < 1024; x += 31)
                    {
                        ad.Lvl.TryGetTile(new TileCoordinates(x, y), out MapTile tile); // if no tile, it will be zeroed out which is what we want
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

        MapTile IMapData.GetTile(Arena arena, TileCoordinates coordinates)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                if (ad.Lvl.TryGetTile(coordinates, out MapTile tile))
                    return tile;

                return MapTile.None;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        MapTile IMapData.GetTile(Arena arena, TileCoordinates coordinates, bool includeTemporaryTiles)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                if (ad.Lvl.TryGetTile(coordinates, out MapTile tile))
                    return tile;

                if (includeTemporaryTiles && ad.TemporaryTileData.TryGetTile(coordinates, out tile))
                    return tile;

                return MapTile.None;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        bool IMapData.TryGetFlagCoordinates(Arena arena, short flagId, out TileCoordinates coordinates)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                return ad.Lvl.TryGetFlagCoordinate(flagId, out coordinates);
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
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

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
                        || (ad.Lvl.TryGetTile(new TileCoordinates(context.X, context.Y), out MapTile tile) && tile != MapTile.None))
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

        int IMapData.GetRegionCount(Arena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                return ad.Lvl.RegionCount;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        MapRegion? IMapData.FindRegionByName(Arena arena, string name)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                return ad.Lvl.FindRegionByName(name);
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        ImmutableHashSet<MapRegion> IMapData.RegionsAt(Arena arena, TileCoordinates location)
        {
            return ((IMapData)this).RegionsAt(arena, location.X, location.Y);
        }

        ImmutableHashSet<MapRegion> IMapData.RegionsAt(Arena arena, short x, short y)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                return ad.Lvl.RegionsAtCoord(x, y);
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        bool IMapData.TryAddBrick(Arena arena, int brickId, TileCoordinates start, TileCoordinates end)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                return ad.TemporaryTileData.TryAddBrick(brickId, start, end);
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        bool IMapData.TryRemoveBrick(Arena arena, int brickId)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                return ad.TemporaryTileData.TryRemoveBrick(brickId);
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        bool IMapData.TryAddDroppedFlag(Arena arena, short flagId, TileCoordinates coordinates)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                return ad.TemporaryTileData.TryAddFlag(flagId, coordinates);
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
        }

        bool IMapData.TryRemoveDroppedFlag(Arena arena, short flagId)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                return ad.TemporaryTileData.TryRemoveFlag(flagId);
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

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

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

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

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
            if (arena is null || !arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.PreCreate)
            {
                _arenaManager.AddHold(arena);

                try
                {
                    // load the level asynchronously
                    LvlData lvlData = await LoadMapAsync(arena).ConfigureAwait(false);

                    // Note: The await is purposely not within the lock
                    // since the lock/unlock has to be performed on the same thread.

                    ad.Lock.EnterWriteLock();

                    try
                    {
                        ad.LvlData = lvlData;
                    }
                    finally
                    {
                        ad.Lock.ExitWriteLock();
                    }
                }
                finally
                {
                    _arenaManager.RemoveHold(arena);
                }
            }
            else if (action == ArenaAction.Destroy)
            {
                LvlData? lvlData;

                ad.Lock.EnterWriteLock();

                try
                {
                    lvlData = ad.LvlData;
                    ad.LvlData = null;
                }
                finally
                {
                    ad.Lock.ExitWriteLock();
                }

                if (lvlData is not null)
                {
                    lock (_lock)
                    {
                        lvlData.Arenas.Remove(arena);

                        if (lvlData != _emergencyMapData && lvlData.Arenas.Count == 0)
                        {
                            if (_lvlDictionary.Remove(lvlData.Id!.Value))
                            {
                                _lvlDataPool.Return(lvlData);
                            }
                        }
                    }
                }
            }
        }

        private async Task<LvlData> LoadMapAsync(Arena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);

            string? path = await ((IMapData)this).GetMapFilenameAsync(arena, null).ConfigureAwait(false);

            ExtendedLvl? lvl = null;

            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    // Open the file on a worker thread.
                    await using FileStream fileStream = await Task.Factory.StartNew(
                        static (obj) => new FileStream((string)obj!, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true),
                        path).ConfigureAwait(false);

                    // Get the checksum.
                    uint checksum;
                    Crc32 crc32 = _objectPoolManager.Crc32Pool.Get();
                    try
                    {
                        await crc32.AppendAsync(fileStream).ConfigureAwait(false);
                        checksum = crc32.GetCurrentHashAsUInt32();
                    }
                    finally
                    {
                        _objectPoolManager.Crc32Pool.Return(crc32);
                    }

                    // Try to get it from the cache.
                    LvlDataId id = new(path, checksum);
                    LvlData? lvlData;

                    lock (_lock)
                    {
                        if (_lvlDictionary.TryGetValue(id, out lvlData))
                        {
                            // Already have it, use the cached data.
                            lvlData.Arenas.Add(arena);
                            return lvlData;
                        }
                    }

                    // Read the file as a lvl.
                    fileStream.Position = 0;
                    lvl = await Task.Factory.StartNew(
                        static (obj) => new ExtendedLvl((FileStream)obj!),
                        fileStream).ConfigureAwait(false);

                    lvlData = _lvlDataPool.Get();
                    lvlData.Initialize(id, lvl);
                    lvlData.Arenas.Add(arena);

                    bool added = false;

                    try
                    {
                        lock (_lock)
                        {
                            if (_lvlDictionary.TryAdd(id, lvlData))
                            {
                                _logManager.LogA(LogLevel.Info, nameof(MapData), arena, $"Successfully processed map file '{path}' with {lvl.TileCount} tiles, {lvl.FlagCount} flags, {lvl.RegionCount} regions, {lvl.Errors.Count} errors");
                                added = true;
                                return lvlData;
                            }
                            else
                            {
                                // Another thread added it at the same time for another arena.
                                if (_lvlDictionary.TryGetValue(id, out LvlData? existingLvlData))
                                {
                                    existingLvlData.Arenas.Add(arena);
                                    return existingLvlData;
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (!added)
                            _lvlDataPool.Return(lvlData);
                    }
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

            // Fall back to the emergency map. This matches the compressed map in MapNewsDownload.cs
            _logManager.LogA(LogLevel.Warn, nameof(MapData), arena, "Using emergency map.");
            lock (_lock)
            {
                _emergencyMapData.Arenas.Add(arena);
            }
            return _emergencyMapData;
        }

        #region Helper types

        private readonly record struct LvlDataId(string Path, uint Checksum);

        private class LvlData : IResettable
        {
            public LvlDataId? Id { get; private set; }
            public ExtendedLvl? Lvl { get; private set; }
            public readonly HashSet<Arena> Arenas = new(Constants.TargetArenaCount);

            public void Initialize(LvlDataId id, ExtendedLvl lvl)
            {
                Id = id;
                Lvl = lvl;
            }

            bool IResettable.TryReset()
            {
                Id = default;
                Lvl = null;
                Arenas.Clear();
                return true;
            }
        }

        /// <summary>
        /// Identifies a temporarily placed map object (brick or flag).
        /// </summary>
        /// <param name="Id">Id of the object (BrickId or FlagId)</param>
        /// <param name="Tile">The type of tile (<see cref="MapTile.Brick"/> or <see cref="MapTile.Flag"/>).</param>
        private readonly record struct TemporaryTileKey(int Id, MapTile Tile);

        /// <summary>
        /// Data about a temporarily placed map object (brick or flag).
        /// </summary>
        /// <param name="Key"></param>
        /// <param name="Start"></param>
        /// <param name="End"></param>
        private readonly record struct TemporaryTilePlacement(TemporaryTileKey Key, TileCoordinates Start, TileCoordinates End);

        /// <summary>
        /// Helper to manage tiles that are temporarily placed (bricks and flags).
        /// </summary>
        private class TemporaryTileData
        {
            private readonly Dictionary<TemporaryTileKey, TemporaryTilePlacement> _placementDictionary = new(Bricks.MaxActiveBricks + CarryFlags.MaxFlags);
            private readonly Dictionary<TileCoordinates, TemporaryTileKey> _tiles = new(8192);
            private readonly object _lock = new();

            public void Clear()
            {
                lock (_lock)
                {
                    _placementDictionary.Clear();
                    _tiles.Clear();
                }
            }

            public bool TryGetTile(TileCoordinates coordinates, out MapTile tile)
            {
                lock (_lock)
                {
                    if (_tiles.TryGetValue(coordinates, out TemporaryTileKey key))
                    {
                        tile = key.Tile;
                        return true;
                    }
                    else
                    {
                        tile = MapTile.None;
                        return false;
                    }
                }
            }

            public bool TryAddBrick(int brickId, TileCoordinates start, TileCoordinates end)
            {
                TemporaryTileKey key = new(brickId, MapTile.Brick);
                TemporaryTilePlacement placement = new(key, start, end);

                lock (_lock)
                {
                    if (!_placementDictionary.TryAdd(key, placement))
                    {
                        if (!TryRemoveBrick(brickId))
                            return false;

                        if (!_placementDictionary.TryAdd(key, placement))
                            return false;
                    }

                    ProcessTiles(
                        key, 
                        start, 
                        end, 
                        static (k, c, s) => s.SetTile(k, c),
                        this);

                    return true;
                }
            }

            public bool TryAddFlag(short flagId, TileCoordinates coordinates)
            {
                TemporaryTileKey key = new(flagId, MapTile.Flag);
                TemporaryTilePlacement placement = new(key, coordinates, coordinates);

                lock (_lock)
                {
                    if (!_placementDictionary.TryAdd(key, placement))
                    {
                        if (!TryRemoveFlag(flagId))
                            return false;

                        if (!_placementDictionary.TryAdd(key, placement))
                            return false;
                    }

                    SetTile(key, coordinates);
                    return true;
                }
            }

            public bool TryRemoveBrick(int brickId)
            {
                return TryRemove(new TemporaryTileKey(brickId, MapTile.Brick));
                
            }

            public bool TryRemoveFlag(short flagId)
            {
                return TryRemove(new TemporaryTileKey(flagId, MapTile.Flag));
            }

            private bool TryRemove(TemporaryTileKey key)
            {
                lock (_lock)
                {
                    if (!_placementDictionary.Remove(key, out TemporaryTilePlacement placement))
                        return false;

                    ProcessTiles(
                        key,
                        placement.Start,
                        placement.End,
                        static (k, c, s) => s.RemoveTile(k.Id, c),
                        this);

                    return true;
                }
            }

            private bool SetTile(TemporaryTileKey key, TileCoordinates coordinates)
            {
                if (_tiles.TryAdd(coordinates, key))
                    return true;

                // A tile already exists at the coordinate.
                if (_tiles.TryGetValue(coordinates, out TemporaryTileKey existing))
                {
                    // A flag can override an existing brick.
                    // A brick can override an existing brick.
                    if ((key.Tile == MapTile.Flag && existing.Tile == MapTile.Brick)
                        && (key.Tile == MapTile.Brick && existing.Tile == MapTile.Brick))
                    {
                        _tiles[coordinates] = key;
                        return true;
                    }
                }

                return false;
            }

            private bool RemoveTile(int id, TileCoordinates coordinates)
            {
                if (!_tiles.TryGetValue(coordinates, out TemporaryTileKey key))
                    return false;

                if (key.Id != id)
                    return false;
                
                return _tiles.Remove(coordinates);
            }

            /// <summary>
            /// Processes a line of tiles from <paramref name="start"/> to <paramref name="end"/>, calling <paramref name="executeCallback"/> for each coordinate.
            /// </summary>
            /// <remarks>The line of tiles is expected to be horizontal or vertical.</remarks>
            /// <typeparam name="T">Type of the state to pass into <paramref name="executeCallback"/>.</typeparam>
            /// <param name="key">Identifies the object that tiles are being processed for, passed to the <paramref name="executeCallback"/>.</param>
            /// <param name="start">The starting coordinate.</param>
            /// <param name="end">The ending coordinate.</param>
            /// <param name="executeCallback">The callback to invoke for each coordinate.</param>
            /// <param name="state">The state to pass to the <paramref name="executeCallback"/>.</param>
            /// <returns>The number of affected tiles.</returns>
            private static int ProcessTiles<T>(TemporaryTileKey key, TileCoordinates start, TileCoordinates end, Func<TemporaryTileKey, TileCoordinates, T, bool> executeCallback, T state)
            {
                int count = 0;

                if (start.X == end.X)
                {
                    short from = start.Y;
                    short to = end.Y;

                    if (from > to)
                    {
                        // Swap
                        (to, from) = (from, to);
                    }

                    for (short y = from; y <= to; y++)
                    {
                        if (executeCallback(key, new TileCoordinates(start.X, y), state))
                            count++;
                    }
                }
                else if (start.Y == end.Y)
                {
                    short from = start.X;
                    short to = end.X;

                    if (from > to)
                    {
                        // Swap
                        (to, from) = (from, to);
                    }

                    for (short x = from; x <= to; x++)
                    {
                        if (executeCallback(key, new TileCoordinates(x, start.Y), state))
                            count++;
                    }
                }

                return count;
            }
        }

        private class ArenaData : IResettable
        {
            public readonly ReaderWriterLockSlim Lock = new();

            public LvlData? LvlData = null;

            /// <summary>
            /// Temporarily placed tiles (bricks and flags)
            /// </summary>
            public readonly TemporaryTileData TemporaryTileData = new();

            public ExtendedLvl? Lvl => LvlData?.Lvl;

            public bool TryReset()
            {
                Lock.EnterWriteLock();

                try
                {
                    LvlData = null;
                    TemporaryTileData.Clear();
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

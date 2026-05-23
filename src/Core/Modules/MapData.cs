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
    public sealed class MapData : IModule, IMapData
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
        private readonly Lock _lock = new();

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

        /// <summary>
        /// This is the pre-computed table for the standard CRC-32 polynomial, 0x04C11DB7.
        /// </summary>
        private static ReadOnlySpan<uint> Crc32Lookup => [
            0X00000000, 0X77073096, 0XEE0E612C, 0X990951BA, 0X076DC419, 0X706AF48F, 0XE963A535, 0X9E6495A3,
            0X0EDB8832, 0X79DCB8A4, 0XE0D5E91E, 0X97D2D988, 0X09B64C2B, 0X7EB17CBD, 0XE7B82D07, 0X90BF1D91,
            0X1DB71064, 0X6AB020F2, 0XF3B97148, 0X84BE41DE, 0X1ADAD47D, 0X6DDDE4EB, 0XF4D4B551, 0X83D385C7,
            0X136C9856, 0X646BA8C0, 0XFD62F97A, 0X8A65C9EC, 0X14015C4F, 0X63066CD9, 0XFA0F3D63, 0X8D080DF5,
            0X3B6E20C8, 0X4C69105E, 0XD56041E4, 0XA2677172, 0X3C03E4D1, 0X4B04D447, 0XD20D85FD, 0XA50AB56B,
            0X35B5A8FA, 0X42B2986C, 0XDBBBC9D6, 0XACBCF940, 0X32D86CE3, 0X45DF5C75, 0XDCD60DCF, 0XABD13D59,
            0X26D930AC, 0X51DE003A, 0XC8D75180, 0XBFD06116, 0X21B4F4B5, 0X56B3C423, 0XCFBA9599, 0XB8BDA50F,
            0X2802B89E, 0X5F058808, 0XC60CD9B2, 0XB10BE924, 0X2F6F7C87, 0X58684C11, 0XC1611DAB, 0XB6662D3D,
            0X76DC4190, 0X01DB7106, 0X98D220BC, 0XEFD5102A, 0X71B18589, 0X06B6B51F, 0X9FBFE4A5, 0XE8B8D433,
            0X7807C9A2, 0X0F00F934, 0X9609A88E, 0XE10E9818, 0X7F6A0DBB, 0X086D3D2D, 0X91646C97, 0XE6635C01,
            0X6B6B51F4, 0X1C6C6162, 0X856530D8, 0XF262004E, 0X6C0695ED, 0X1B01A57B, 0X8208F4C1, 0XF50FC457,
            0X65B0D9C6, 0X12B7E950, 0X8BBEB8EA, 0XFCB9887C, 0X62DD1DDF, 0X15DA2D49, 0X8CD37CF3, 0XFBD44C65,
            0X4DB26158, 0X3AB551CE, 0XA3BC0074, 0XD4BB30E2, 0X4ADFA541, 0X3DD895D7, 0XA4D1C46D, 0XD3D6F4FB,
            0X4369E96A, 0X346ED9FC, 0XAD678846, 0XDA60B8D0, 0X44042D73, 0X33031DE5, 0XAA0A4C5F, 0XDD0D7CC9,
            0X5005713C, 0X270241AA, 0XBE0B1010, 0XC90C2086, 0X5768B525, 0X206F85B3, 0XB966D409, 0XCE61E49F,
            0X5EDEF90E, 0X29D9C998, 0XB0D09822, 0XC7D7A8B4, 0X59B33D17, 0X2EB40D81, 0XB7BD5C3B, 0XC0BA6CAD,
            0XEDB88320, 0X9ABFB3B6, 0X03B6E20C, 0X74B1D29A, 0XEAD54739, 0X9DD277AF, 0X04DB2615, 0X73DC1683,
            0XE3630B12, 0X94643B84, 0X0D6D6A3E, 0X7A6A5AA8, 0XE40ECF0B, 0X9309FF9D, 0X0A00AE27, 0X7D079EB1,
            0XF00F9344, 0X8708A3D2, 0X1E01F268, 0X6906C2FE, 0XF762575D, 0X806567CB, 0X196C3671, 0X6E6B06E7,
            0XFED41B76, 0X89D32BE0, 0X10DA7A5A, 0X67DD4ACC, 0XF9B9DF6F, 0X8EBEEFF9, 0X17B7BE43, 0X60B08ED5,
            0XD6D6A3E8, 0XA1D1937E, 0X38D8C2C4, 0X4FDFF252, 0XD1BB67F1, 0XA6BC5767, 0X3FB506DD, 0X48B2364B,
            0XD80D2BDA, 0XAF0A1B4C, 0X36034AF6, 0X41047A60, 0XDF60EFC3, 0XA867DF55, 0X316E8EEF, 0X4669BE79,
            0XCB61B38C, 0XBC66831A, 0X256FD2A0, 0X5268E236, 0XCC0C7795, 0XBB0B4703, 0X220216B9, 0X5505262F,
            0XC5BA3BBE, 0XB2BD0B28, 0X2BB45A92, 0X5CB36A04, 0XC2D7FFA7, 0XB5D0CF31, 0X2CD99E8B, 0X5BDEAE1D,
            0X9B64C2B0, 0XEC63F226, 0X756AA39C, 0X026D930A, 0X9C0906A9, 0XEB0E363F, 0X72076785, 0X05005713,
            0X95BF4A82, 0XE2B87A14, 0X7BB12BAE, 0X0CB61B38, 0X92D28E9B, 0XE5D5BE0D, 0X7CDCEFB7, 0X0BDBDF21,
            0X86D3D2D4, 0XF1D4E242, 0X68DDB3F8, 0X1FDA836E, 0X81BE16CD, 0XF6B9265B, 0X6FB077E1, 0X18B74777,
            0X88085AE6, 0XFF0F6A70, 0X66063BCA, 0X11010B5C, 0X8F659EFF, 0XF862AE69, 0X616BFFD3, 0X166CCF45,
            0XA00AE278, 0XD70DD2EE, 0X4E048354, 0X3903B3C2, 0XA7672661, 0XD06016F7, 0X4969474D, 0X3E6E77DB,
            0XAED16A4A, 0XD9D65ADC, 0X40DF0B66, 0X37D83BF0, 0XA9BCAE53, 0XDEBB9EC5, 0X47B2CF7F, 0X30B5FFE9,
            0XBDBDF21C, 0XCABAC28A, 0X53B39330, 0X24B4A3A6, 0XBAD03605, 0XCDD70693, 0X54DE5729, 0X23D967BF,
            0XB3667A2E, 0XC4614AB8, 0X5D681B02, 0X2A6F2B94, 0XB40BBE37, 0XC30C8EA1, 0X5A05DF1B, 0X2D02EF8D
        ];

        bool IMapData.TryGetCrc32(Arena arena, out uint crc32)
        {
            ArgumentNullException.ThrowIfNull(arena);

            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            ad.Lock.EnterReadLock();

            try
            {
                if (ad.Lvl is null)
                    throw new InvalidOperationException(Error_ArenaDataNotLoaded);

                crc32 = uint.MaxValue;

                for (short y = 0; y < 1024; y++)
                {
                    for (short x = 0; x < 1024; x++)
                    {
                        if (ad.Lvl.TryGetTile(new TileCoordinates(x, y), out MapTile tile)
                            && (tile >= MapTile.TileStart && (tile <= MapTile.TileEnd || tile.IsSafe)))
                        {
                            crc32 = Crc32Lookup[(byte)tile ^ (byte)crc32] ^ crc32 >> 8;
                        }
                    }
                }

                return true;
            }
            finally
            {
                ad.Lock.ExitReadLock();
            }
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
            private readonly Lock _lock = new();

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

using SS.Core.Map;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Info about an lvz file.
    /// </summary>
    public readonly struct LvzFileInfo
    {
        public readonly string Filename;
        public readonly bool IsOptional;

        public LvzFileInfo(string filename, bool isOptional)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filename);

            Filename = filename;
            IsOptional = isOptional;
        }
    }

    public interface IMapData : IComponentInterface
    {
        #region File Info

        /// <summary>
        /// Gets the path of a map file used by an arena.
        /// </summary>
        /// <remarks>
        /// This function should be used rather than try to figure out the map filename yourself based on arena settings.
        /// </remarks>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="mapname"><see langword="null"/> if you're looking for an lvl, or the name of an lvz file.</param>
        /// <returns>The path if the lvl or lvz file could be found; otherwise, <see langword="null"/>.</returns>
        string? GetMapFilename(Arena arena, string? mapname);

        /// <summary>
        /// Gets info about lvz files in use by an arena.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <returns>A collection of lvz file info.</returns>
        IEnumerable<LvzFileInfo> LvzFilenames(Arena arena);

        #endregion

        #region LVL Info

        /// <summary>
        /// Gets a named attribute of the map in an arena.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="key">The key of the attribute to retrieve.</param>
        /// <returns>The value if found; otherwise, <see langword="null"/>.</returns>
        string? GetAttribute(Arena arena, string key);

        /// <summary>
        /// Gets unprocessed chunk data of a specified type for a the map in an arena.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="chunkType">The type of data to retrieve.</param>
        /// <returns>A collection of chunk payloads (chunk header not included).</returns>
        IEnumerable<ReadOnlyMemory<byte>> ChunkData(Arena arena, uint chunkType);

        /// <summary>
        /// Gets the number of tiles on the map in an arena.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <returns>The # of tiles.</returns>
        int GetTileCount(Arena arena);

        /// <summary>
        /// Gets the number of turf (static) flags on the map in an arena.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <returns>The # of flags.</returns>
        int GetFlagCount(Arena arena);

        /// <summary>
        /// Gets non-fatal load error descriptions for the map in an arena.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <returns>A collection of error descriptions.</returns>
        IReadOnlyList<string> GetErrors(Arena arena);

        /// <summary>
        /// Get the map checksum.
        /// </summary>
        /// <remarks>
        /// Used by the Security module to validate clients.
        /// Used by the Recording module to make sure the recording plays on the same map that is was recorded on.
        /// </remarks>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="key"></param>
        /// <returns></returns>
        uint GetChecksum(Arena arena, uint key);

        #endregion

        #region LVL Tile / Coordinates Methods

        /// <summary>
        /// Gets a single tile by coordinates.
        /// </summary>
        /// <remarks>
        /// This does not include any temporarily placed tiles (bricks and dropped flags).
        /// Use <see cref="GetTile(Arena, MapCoordinate, bool)"/> to access temporary tiles.
        /// </remarks>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="coordinate">The coordinate to check.</param>
        /// <returns>The tile. <see cref="MapTile.None"/> indicates no tile.</returns>
        MapTile GetTile(Arena arena, MapCoordinate coordinate);

        /// <summary>
        /// Gets a single tile by coordinates.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="coordinate">The coordinate to check.</param>
        /// <param name="includeTemporaryTiles">Whether to include temporary tiles such as bricks and dropped flags.</param>
        /// <returns>The tile. <see cref="MapTile.None"/> indicates no tile.</returns>
        MapTile GetTile(Arena arena, MapCoordinate coordinate, bool includeTemporaryTiles);

        /// <summary>
        /// Gets the coordinate of a static, turf-style flag.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="flagId">Id of the flag to get the coordinate of.</param>
        /// <param name="coordinate">The coordinate of the flag.</param>
        /// <returns><see langword="true"/> if the coordinate could be retrieved. Otherwise, <see langword="false"/>.</returns>
        bool TryGetFlagCoordinate(Arena arena, short flagId, out MapCoordinate coordinate);

        /// <summary>
        /// Tries to find an empty tile nearest to the given coordinates.
        /// </summary>
        /// <param name="arena">The arena.</param>
        /// <param name="x">The X-coordinate to start searching from. Upon return, the resulting X-coordinate if an empty spot was found.</param>
        /// <param name="y">The Y-Coordinate to start searching from. Upon return, the resulting Y-coordinate if an empty spot was found.</param>
        /// <returns>
        /// True if a coordinate with no tile was found. Otherwise, false.
        /// </returns>
        bool TryFindEmptyTileNear(Arena arena, ref short x, ref short y);

        #endregion

        #region LVL Regions

        /// <summary>
        /// Gets the number of regions on a map in an arena.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <returns>The # of regions.</returns>
        int GetRegionCount(Arena arena);

        /// <summary>
        /// Gets a region by name.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="name">Name of the region to search for.</param>
        /// <returns>The region if found; otherwise, <see langword="null"/>.</returns>
        MapRegion? FindRegionByName(Arena arena, string name);

        /// <summary>
        /// To get the regions that are at a specific coordinate.
        /// </summary>
        /// <remarks>Similar to asss' Imapdata.EnumContaining, but without using a callback.</remarks>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="coordinate">The coordinates to check.</param>
        /// <returns>A set of regions (empty if the coordinate is not in a region).</returns>
        ImmutableHashSet<MapRegion> RegionsAt(Arena arena, MapCoordinate coordinate);

        /// <summary>
        /// To get the regions that are at a specific coordinate.
        /// </summary>
        /// <remarks>Similar to asss' Imapdata.EnumContaining, but without using a callback.</remarks>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="x">X coordinate to check.</param>
        /// <param name="y">Y coordinate to check.</param>
        /// <returns>A set of regions (empty if the coordinate is not in a region).</returns>
        ImmutableHashSet<MapRegion> RegionsAt(Arena arena, short x, short y);

        #endregion

        #region Temporary Tile Placement

        /// <summary>
        /// Adds temporarily placed map tile(s) for a brick.
        /// </summary>
        /// <param name="arena">The arena to add temporarily placed tile(s) to.</param>
        /// <param name="brickId">The Id of the brick.</param>
        /// <param name="start">The starting coordinates of the brick.</param>
        /// <param name="end">The ending coordinates of the brick.</param>
        /// <returns><see langword="true"/> if a change was made; otherwise, <see langword="false"/>.</returns>
        bool TryAddBrick(Arena arena, int brickId, MapCoordinate start, MapCoordinate end);

        /// <summary>
        /// Removes temporarily placed map tile(s) for a brick.
        /// </summary>
        /// <param name="arena">The arena to remove the temporarily placed tiles from.</param>
        /// <param name="brickId">The Id of the brick.</param>
        /// <returns><see langword="true"/> if a change was made; otherwise, <see langword="false"/>.</returns>
        bool TryRemoveBrick(Arena arena, int brickId);

        /// <summary>
        /// Adds a temporarily placed map tile for a carriable flag that was dropped on the map. 
        /// </summary>
        /// <remarks>
        /// If the flag is already on the map, it will be moved.
        /// </remarks>
        /// <param name="arena">The arena to add the temporarily placed tile to.</param>
        /// <param name="flagId">The Id of the flag.</param>
        /// <param name="coordinate">The coordinate the flag is being dropped at.</param>
        /// <returns><see langword="true"/> if a change was made; otherwise, <see langword="false"/>.</returns>
        bool TryAddDroppedFlag(Arena arena, short flagId, MapCoordinate coordinate);

        /// <summary>
        /// Removes a temporarily placed map tile for a carriable flag that was dropped on the map.
        /// </summary>
        /// <param name="arena">The arena to remove the temporarily placed tile from.</param>
        /// <param name="flagId">The Id of the flag.</param>
        /// <returns><see langword="true"/> if a change was made; otherwise, <see langword="false"/>.</returns>
        bool TryRemoveDroppedFlag(Arena arena, short flagId);

        #endregion

        #region Image Generation

        /// <summary>
        /// Saves an image of the map to a file.
        /// </summary>
        /// <param name="arena">The arena of the map to save.</param>
        /// <param name="path">The path of the file. The image format is automatically determined based on the filename extension.</param>
        /// <exception cref="ArgumentNullException">The <paramref name="arena"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The <paramref name="path"/> is null or white-space.</exception>
        /// <exception cref="ArgumentException">The <paramref name="path"/> file extension specifies an unsupported image format.</exception>
        /// <exception cref="Exception">Error encoding image.</exception>
        void SaveImage(Arena arena, string path);

        /// <summary>
        /// Saves an image of the map to a file.
        /// </summary>
        /// <param name="arena">The arena of the map to save.</param>
        /// <param name="stream">The stream to write the image data to.</param>
        /// <param name="imageFormat">The format to save the image as. Supported formats: 'png', 'jpg', and 'webp'.</param>
        /// <exception cref="ArgumentNullException">The <paramref name="arena"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException">The <paramref name="stream"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Unsupported image format for the provided <paramref name="imageFormat"/>.</exception>
        /// <exception cref="Exception">Error encoding image.</exception>
        void SaveImage(Arena arena, Stream stream, ReadOnlySpan<char> imageFormat);

        #endregion
    }
}

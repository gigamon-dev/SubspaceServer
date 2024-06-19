using SS.Core.Map;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

namespace SS.Core.ComponentInterfaces
{
    public struct LvzFileInfo
    {
        public string Filename { get; init; }
        public bool IsOptional { get; init; }

        public LvzFileInfo(string filename, bool isOptional)
        {
            if (string.IsNullOrWhiteSpace(filename))
                throw new ArgumentException("Cannot be null or white-space.", nameof(filename));

            Filename = filename;
            IsOptional = isOptional;
        }
    }

    public interface IMapData : IComponentInterface
    {
        /// <summary>
        /// Gets the path of a map file used by an arena.
        /// </summary>
        /// <remarks>
        /// This function should be used rather than try to figure out the map filename yourself based on arena settings.
        /// </remarks>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="mapname">null if you're looking for an lvl, or the name of an lvz file.</param>
        /// <returns>The path if the lvl or lvz file could be found; otherwise, null.</returns>
        string GetMapFilename(Arena arena, string mapname);

        /// <summary>
        /// Gets unprocessed chunk data of a specified type for a the map in an arena.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="chunkType">The type of data to retrieve.</param>
        /// <returns>A collection of chunk payloads (chunk header not included).</returns>
        IEnumerable<ReadOnlyMemory<byte>> ChunkData(Arena arena, uint chunkType);

        /// <summary>
        /// Gets info about lvz files in use by an arena.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <returns>A collection of lvz file info.</returns>
        IEnumerable<LvzFileInfo> LvzFilenames(Arena arena);

        /// <summary>
        /// Gets a named attribute of the map in an arena.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="key">The key of the attribute to retrieve.</param>
        /// <returns>The value, or NULL if not present.</returns>
        string GetAttribute(Arena arena, string key);

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
        /// Gets a single tile by coordinates.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="coord">The coordinate to check.</param>
        /// <returns>The tile, null for no tile.</returns>
        MapTile? GetTile(Arena arena, MapCoordinate coord);

        /// <summary>
        /// Gets the coordinate of a static, turf-style flag.
        /// </summary>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="flagId">Id of the flag to get the coordinate of.</param>
        /// <param name="coordinate">The coordinate of the flag.</param>
        /// <returns><see langword="true"/> if the coordinate could be retrieved. Otherwise, <see langword="false"/>.</returns>
        bool TryGetFlagCoordinate(Arena arena, short flagId, out MapCoordinate coordinate);

        /// <summary>
        /// Tries to find an empty tile nearest to the given coords.
        /// </summary>
        /// <param name="arena">The arena.</param>
        /// <param name="x">The X-coordinate to start searching from. Upon return, the resulting X-coordinate if an empty spot was found.</param>
        /// <param name="y">The Y-Coordinate to start searching from. Upon return, the resulting Y-coordinate if an empty spot was found.</param>
        /// <returns>
        /// True if a coordinate with no tile was found. Otherwise, false.
        /// </returns>
        bool TryFindEmptyTileNear(Arena arena, ref short x, ref short y);

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
        /// <returns>The region if found; otherwise, null.</returns>
        MapRegion FindRegionByName(Arena arena, string name);

        /// <summary>
        /// To get the regions that are at a specific coordinate.
        /// </summary>
        /// <remarks>Similar to asss' Imapdata.EnumContaining, but without using a callback.</remarks>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="x">X coordinate to check.</param>
        /// <param name="y">Y coordinate to check.</param>
        /// <returns>A set of regions (empty if the coordinate is not in a region).</returns>
        ImmutableHashSet<MapRegion> RegionsAt(Arena arena, short x, short y);

        /// <summary>
        /// To get the regions that are at a specific coordinate.
        /// </summary>
        /// <remarks>Similar to asss' Imapdata.EnumContaining, but without using a callback.</remarks>
        /// <param name="arena">The arena to retrieve the map info for.</param>
        /// <param name="coord">The coordinates to check.</param>
        /// <returns>A set of regions (empty if the coordinate is not in a region).</returns>
        ImmutableHashSet<MapRegion> RegionsAt(Arena arena, MapCoordinate coord);

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
    }
}

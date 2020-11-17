using SS.Core.Map;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace SS.Core.ComponentInterfaces
{
    public struct LvzFileInfo
    {
        public string Filename;
        public bool IsOptional;

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
        /// finds the file currently used as this arena's map.
        /// you should use this function and not try to figure out the map
        /// filename yourself based on arena settings.
        /// </summary>
        /// <param name="arena">the arena whose map we want</param>
        /// <param name="filename">the resulting filename</param>
        /// <param name="mapname">null if you're looking for an lvl, or the name of an lvz file.</param>
        /// <returns>true if it could find a lvl or lvz file, buf will contain the result. false if it failed.</returns>
        string GetMapFilename(Arena arena, string mapname);

        IEnumerable<ReadOnlyMemory<byte>> ChunkData(Arena arena, uint chunkType);

        IEnumerable<LvzFileInfo> LvzFilenames(Arena arena);

        /// <summary>
        /// gets the named attribute for the arena's map.
        /// </summary>
        /// <param name="arena">the arena whose map we care about.</param>
        /// <param name="key">the attribute key to retrieve.</param>
        /// <returns>the key's value, or NULL if not present</returns>
        string GetAttribute(Arena arena, string key);

        /// <summary>
        /// To get the number of turf (static) flags on the map in an arena
        /// </summary>
        /// <param name="arena">the arena whose map we care about</param>
        /// <returns>the # of turf flags</returns>
        int GetFlagCount(Arena arena);

        /// <summary>
        /// To get the contents of a single tile of the map.
        /// </summary>
        /// <param name="arena">the arena whose map we care about</param>
        /// <param name="coord">coordinates looking at</param>
        /// <returns>the tile, null for no tile</returns>
        MapTile? GetTile(Arena arena, MapCoordinate coord);

        bool FindEmptyTileNear(Arena arena, ref short x, ref short y);

        bool FindEmptyTileInRegion(Arena arena, MapRegion region);

        /// <summary>
        /// Get the map checksum
        /// <remarks>Used by Recording module to make sure the recording plays on the same map that is was recorded on.</remarks>
        /// </summary>
        /// <param name="arena"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        uint GetChecksum(Arena arena, uint key);

        int GetRegionCount(Arena arena);

        MapRegion FindRegionByName(Arena arena, string name);

        /// <summary>
        /// To get the regions that are at a specific coordinate.
        /// </summary>
        /// <remarks>Similar to asss' Imapdata.EnumContaining, but without using a callback.</remarks>
        /// <param name="arena"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        IImmutableSet<MapRegion> RegionsAt(Arena arena, short x, short y);

        /// <summary>
        /// To get the regions that are at a specific coordinate.
        /// </summary>
        /// <remarks>Similar to asss' Imapdata.EnumContaining, but without using a callback.</remarks>
        /// <param name="arena"></param>
        /// <param name="coord"></param>
        /// <returns></returns>
        IImmutableSet<MapRegion> RegionsAt(Arena arena, MapCoordinate coord);
    }
}

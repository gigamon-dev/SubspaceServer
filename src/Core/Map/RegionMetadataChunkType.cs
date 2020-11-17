using System.Buffers.Binary;
using System.Text;

namespace SS.Core.Map
{
    /// <summary>
    /// other chunk types used specially for regions
    /// </summary>
    public static class RegionMetadataChunkType
    {
        /// <summary>
        /// name of the region
        /// this is just a plain ascii string, not null terminated. every chunk
        /// should have exactly one of these.
        /// </summary>
        public static readonly uint Name = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes("rNAM"));

        /// <summary>
        /// this subchunk describes the tiles that make up the region. it's stored
        /// in a compact rle-ish representation.
        /// </summary>
        public static readonly uint TileData = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes("rTIL"));

        /// <summary>
        /// this is a 0-byte chunk. its presence signifies that this region should be considered a "base".
        /// </summary>
        public static readonly uint IsBase = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes("rBSE"));

        /// <summary>
        /// this is a 0-byte chunk. if present, antiwarp should be disabled for
        /// ships in this region. currently, this disabling happens on the server,
        /// and players whose antiwarp is being disabled aren't necessarily aware of
        /// it. it would be nice if in the future continuum understood this feature
        /// so that it could inform the player that antiwarp is unavailable in this
        /// location.
        /// </summary>
        public static readonly uint NoAntiwarp = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes("rNAW"));

        /// <summary>
        /// this is a 0-byte chunk. if present, all weapons are non-functional in
        /// this region. the same notes apply to this feature as to the no antiwarp
        /// feature.
        /// </summary>
        public static readonly uint NoWeapons = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes("rNWP"));

        /// <summary>
        /// this is a 0-byte chunk. if present, any flags dropped in this region are
        /// respawned as neutral flags in the center of the map (or wherever the
        /// settings indicate they should be spawned).
        /// </summary>
        public static readonly uint NoFlagDrops = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes("rNFL"));

        /// <summary>
        /// this chunk, if present, turns on the auto-warping feature. any player
        /// entering this region will be immediately warped to the specified
        /// destination.
        /// </summary>
        public static readonly uint Autowarp = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes("rAWP"));

        /// <summary>
        /// Embedded Python code.
        /// <para>
        /// this chunk should contain ascii data representing some python code. the
        /// code will be executed when the map is loaded, and it may define several
        /// functions: a function named "enter", if it exists, will be call each
        /// time a player enters this region.it will be called with one argument,
        /// which is the player who entered the region. a function named "exit"
        /// works similarly, except of course it gets called when someone leaves.
        /// </para>
        /// </summary>
        public static readonly uint PythonCode = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes("rPYC"));
    }
}

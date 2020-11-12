using System.Buffers.Binary;
using System.Text;

namespace SS.Core.Map
{
    public static class MapMetadataChunkType
    {
        /// <summary>
        /// these define miscelaneous textual attributes. the format is ascii text,
        /// not null terminated, in this form: "&lt;key&gt;=&lt;value&gt;". each "ATTR" chunk
        /// should contain just one key/value pair. multiple chunks of this type may
        /// be present in one file.
        /// 
        /// some keys that might be typically found in a level file are:
        /// 
        /// "NAME" - a descriptive name for this map
        /// "VERSION" - a version number for this map
        /// "ZONE" - the zone this file is intended to be used with
        /// "MAPCREATOR" - the person who created this map (the format of the value
        /// should be "name &lt;email&gt;")
        /// "TILESETCREATOR" - the person who created the tileset
        /// "PROGRAM" - the name of the program that was used to create this level
        /// file
        /// </summary>
        public static readonly uint ATTR = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes("ATTR"));

        /// <summary>
        /// these chunks define regions. to recap, a region is a set of tiles,
        /// usually but not always contiguous, with certain properties. asss
        /// understands regions and can implement some advanced features using them.
        /// currently continuum doesn't understand regions, but it would be nice if
        /// it did, and we'll be able to do even cooler things when it does.
        /// 
        /// there's a lot of stuff that you might want to say about a region, so to
        /// support all the varied uses, and also future uses, we'll use the chunk
        /// model again: each region gets its own set of "subchunks" describing its
        /// function. to avoid confusion, all sub-chunk types that go inside the
        /// "REGN" superchunk start with "r". the data of the big "REGN" chunk is
        /// simply a series of subchunks.
        /// 
        /// here are some defined sub-chunk types:
        /// 
        /// "rNAM" - a descriptive name for the region
        /// "rTIL" - tile data, the definition of the region
        /// "rBSE" - whether the region represents a base in a flag game
        /// "rNAW" - no antiwarp
        /// "rNWP" - no weapons
        /// "rNFL" - no flag drops
        /// "rAWP" - auto-warp
        /// "rPYC" - code to be executed when a player enters or leaves this region
        /// </summary>
        public static readonly uint REGN = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes("REGN"));

        /// <summary>
        /// the format of a "TSET" chunk is a windows format bitmap, _without_ the
        /// bitmapfileheader stuff (because its function is taken care of by the
        /// chunk header). that is, it starts with a bitmapinfoheader, which is
        /// followed directly by the color table, which is followed directly by the
        /// bitmap data. the bitmap data should be in 8-bit format with no
        /// compression.
        /// <remarks>for future use when ELVL format is standard and backwards compatability is no longer required</remarks>
        /// </summary>
        public static readonly uint TSET = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes("TSET"));

        /// <summary>
        /// the format of a "TILE" chunk is just tile data, in the same format it's
        /// always been in
        /// <remarks>for future use when ELVL format is standard and backwards compatability is no longer required</remarks>
        /// </summary>
        public static readonly uint TILE = BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes("TILE"));

        /// <summary>
        /// other chunk types used specially for regions
        /// </summary>
        public static class RegionChunkType
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
            /// this chunk should contain ascii data representing some python code.the
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
}

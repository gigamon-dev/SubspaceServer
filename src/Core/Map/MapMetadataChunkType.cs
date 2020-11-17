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
    }
}

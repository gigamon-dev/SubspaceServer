namespace SS.Core.Map
{
    /// <summary>
    /// Keys for map attributes.  These are the ones that are documented in the extended lvl format specification.
    /// </summary>
    public static class MapAttributeKeys
    {
        /// <summary>
        /// A descriptive name for the map.
        /// </summary>
        public const string Name = "NAME";

        /// <summary>
        /// The version number for the map.
        /// </summary>
        public const string Version = "VERSION";

        /// <summary>
        /// The zone the map is intended to be used with.
        /// </summary>
        public const string Zone = "ZONE";

        /// <summary>
        /// The person who created the map.
        /// <para>The format of the value should be "name &lt;email&gt;"</para>
        /// </summary>
        public const string MapCreator = "MAPCREATOR";

        /// <summary>
        /// The person who created the tileset.
        /// </summary>
        public const string TilesetCreator = "TILESETCREATOR";

        /// <summary>
        /// The name of the program that was used to create the level file.
        /// </summary>
        public const string Program = "PROGRAM";
    }
}

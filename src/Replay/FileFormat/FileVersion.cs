namespace SS.Replay.FileFormat
{
    /// <summary>
    /// The recording file version, stored in <see cref="FileHeader.Version"/>.
    /// </summary>
    public static class FileVersion
    {
        /// <summary>
        /// The current version that new recordings will use.
        /// </summary>
        public const int Current = Afluxion;

        /// <summary>
        /// The original ASSS record module version.
        /// </summary>
        public const int ASSS = 2;

        /// <summary>
        /// The version used by the Powerball Zone fork of the ASSS record module.
        /// </summary>
        public const int PB = 2;

        /// <summary>
        /// The version used by the Hockey Zone fork of the ASSS record module.
        /// </summary>
        public const int HZ = 102;

        /// <summary>
        /// The version used by the Hockey Zone fork of the ASSS record module, with latency enhancements.
        /// </summary>
        public const int HZ_Latency = 103;

        /// <summary>
        /// Version used by this zone server, Subspace Server .NET (codename "afluxion").
        /// </summary>
        public const int Afluxion = 200;
    }
}

namespace SS.Core.Configuration
{
    /// <summary>
    /// A configuration line and the file it came from.
    /// </summary>
    public class LineReference
    {
        /// <summary>
        /// The line.
        /// </summary>
        public RawLine Line { get; init; }

        /// <summary>
        /// The file the line came from.
        /// </summary>
        public ConfFile File { get; init; }
    }
}

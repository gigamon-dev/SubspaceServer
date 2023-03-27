using System;

namespace SS.Core.Configuration
{
    /// <summary>
    /// A configuration line and the file it came from.
    /// </summary>
    public class LineReference
    {
        public LineReference(RawLine line, ConfFile file)
        {
            Line = line ?? throw new ArgumentNullException(nameof(line));
            File = file ?? throw new ArgumentNullException(nameof(file));
        }

        /// <summary>
        /// The line.
        /// </summary>
        public RawLine Line { get; }

        /// <summary>
        /// The file the line came from.
        /// </summary>
        public ConfFile File { get; }
    }
}

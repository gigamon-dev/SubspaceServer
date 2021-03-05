using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SS.Core.Configuration
{
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

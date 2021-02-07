using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SS.Core.Configuration
{
    public class LineReference
    {
        public RawLine Line { get; init; }
        public ConfFile File { get; init; }
    }
}

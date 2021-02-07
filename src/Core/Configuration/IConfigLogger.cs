using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SS.Core.Configuration
{
    public interface IConfigLogger
    {
        void Log(LogLevel level, string message);
    }
}

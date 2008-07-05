using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core
{
    public delegate void ConfigChangedDelegate(object clos);

    /// <summary>
    /// other modules should manipulate config files through ConfigHandles
    /// TODO: I'm thinking of removing this class, it seems like a waste and complicates matters, why not just put the callback in the ConfigFile class
    /// </summary>
    public class ConfigHandle
    {
        internal ConfigFile file;
        internal ConfigChangedDelegate func;
        internal object clos;

        internal ConfigHandle(ConfigFile file, ConfigChangedDelegate func, object clos)
        {
            this.file = file;
            this.func = func;
            this.clos = clos;

            file.Lock();
            file.Handles.AddLast(this);
            file.Unlock();
        }
    }
}

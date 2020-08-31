using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public interface IModuleLoader : IComponentInterface
    {
        bool LoadModulesFromConfig(string moduleConfigFilename);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for getting information about config settings.
    /// </summary>
    public interface IConfigHelp : IComponentInterface
    {
        /// <summary>
        /// Gets a lookup of settings grouped by section.
        /// </summary>
        ILookup<string, (ConfigHelpAttribute Attr, string ModuleTypeName)> Sections { get; }
    }
}

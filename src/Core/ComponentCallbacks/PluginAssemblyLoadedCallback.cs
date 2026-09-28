using System.Reflection;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a plug-in assembly is loaded by the <see cref="ModuleManager"/>.
    /// </summary>
    /// <param name="assembly">The plug-in assembly that was loaded.</param>
    [ComponentCallback]
    public delegate void PluginAssemblyLoadedCallback(Assembly assembly);
}

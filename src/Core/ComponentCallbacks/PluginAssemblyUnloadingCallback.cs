using System.Reflection;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a plug-in assembly is about to be unloaded by the <see cref="ModuleManager"/>.
    /// </summary>
    /// <param name="assembly">The plug-in assembly that is being unloaded.</param>
    [ComponentCallback]
    public delegate void PluginAssemblyUnloadingCallback(Assembly assembly);
}

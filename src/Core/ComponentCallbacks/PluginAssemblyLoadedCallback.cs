using System.Reflection;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="PluginAssemblyLoadedDelegate"/>.
    /// </summary>
    [CallbackHelper]
    public static partial class PluginAssemblyLoadedCallback
    {
        /// <summary>
        /// Delegate for a callback when a plug-in assembly is loaded by the <see cref="ModuleManager"/>.
        /// </summary>
        /// <param name="assembly">The plug-in assembly that was loaded.</param>
        public delegate void PluginAssemblyLoadedDelegate(Assembly assembly);
    }
}

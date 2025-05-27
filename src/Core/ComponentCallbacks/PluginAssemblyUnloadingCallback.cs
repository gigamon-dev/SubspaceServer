using System.Reflection;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="PluginAssemblyUnloadingDelegate"/>.
    /// </summary>
    [CallbackHelper]
    public static partial class PluginAssemblyUnloadingCallback
    {
        /// <summary>
        /// Delegate for a callback when a plug-in assembly is about to be unloaded by the <see cref="ModuleManager"/>.
        /// </summary>
        /// <param name="assembly">The plug-in assembly that is being unloaded.</param>
        public delegate void PluginAssemblyUnloadingDelegate(Assembly assembly);
    }
}

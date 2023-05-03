using System.Reflection;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="PluginAssemblyUnloadingDelegate"/>.
    /// </summary>
    public static class PluginAssemblyUnloadingCallback
    {
        /// <summary>
        /// Delegate for a callback when a plug-in assembly is about to be unloaded by the <see cref="ModuleManager"/>.
        /// </summary>
        /// <param name="assembly">The plug-in assembly that is being unloaded.</param>
        public delegate void PluginAssemblyUnloadingDelegate(Assembly assembly);

        public static void Register(ComponentBroker broker, PluginAssemblyUnloadingDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PluginAssemblyUnloadingDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Assembly assembly)
        {
            broker?.GetCallback<PluginAssemblyUnloadingDelegate>()?.Invoke(assembly);

            if (broker?.Parent != null)
                Fire(broker.Parent, assembly);
        }
    }
}

using SS.Core.ComponentInterfaces;
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

        public static void Register(IComponentBroker broker, PluginAssemblyUnloadingDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, PluginAssemblyUnloadingDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Assembly assembly)
        {
            broker?.GetCallback<PluginAssemblyUnloadingDelegate>()?.Invoke(assembly);

            if (broker?.Parent != null)
                Fire(broker.Parent, assembly);
        }
    }
}

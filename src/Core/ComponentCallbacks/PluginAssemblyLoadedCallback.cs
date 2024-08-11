using SS.Core.ComponentInterfaces;
using System.Reflection;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="PluginAssemblyLoadedDelegate"/>.
    /// </summary>
    public static class PluginAssemblyLoadedCallback
    {
        /// <summary>
        /// Delegate for a callback when a plug-in assembly is loaded by the <see cref="ModuleManager"/>.
        /// </summary>
        /// <param name="assembly">The plug-in assembly that was loaded.</param>
        public delegate void PluginAssemblyLoadedDelegate(Assembly assembly);

        public static void Register(IComponentBroker broker, PluginAssemblyLoadedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, PluginAssemblyLoadedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Assembly assembly)
        {
            broker?.GetCallback<PluginAssemblyLoadedDelegate>()?.Invoke(assembly);

            if (broker?.Parent != null)
                Fire(broker.Parent, assembly);
        }
    }
}

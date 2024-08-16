using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Example.ModuleLifeCycleExamples;

/// <summary>
/// This is an example of hooking into the PostLoad and PreUnload steps of the module life-cycle.
/// </summary>
/// <remarks>
/// Configure the module to be attached in the arena.conf file:
/// <code>
/// [ Modules ]
/// AttachModules = Example.ModuleLifeCycleExamples.LoaderAwareExample
/// </code>
/// </remarks>
public class LoaderAwareExample : IModule, IModuleLoaderAware
{
    public bool Load(IComponentBroker broker)
    {
        return true;
    }

    public void PostLoad(IComponentBroker broker)
    {
        // Do something after all modules have been loaded.
    }

    public void PreUnload(IComponentBroker broker)
    {
        // Do something before all modules are to be unloaded.
    }

    public bool Unload(IComponentBroker broker)
    {
        return true;
    }
}

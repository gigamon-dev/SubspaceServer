using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Example.ModuleLifeCycleExamples;

/// <summary>
/// This is an example of the simplest form of loading and unloading a module.
/// </summary>
public sealed class ExampleModule : IModule
{
    bool IModule.Load(IComponentBroker broker)
    {
        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        return true;
    }
}
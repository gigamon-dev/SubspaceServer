using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Example.ModuleLifeCycleExamples;

/// <summary>
/// This is an example of hooking into the AttachModule and DetachModule steps of the module life-cycle.
/// </summary>
public class ArenaAttachableExample : IModule, IArenaAttachableModule
{
    public bool Load(IComponentBroker broker)
    {
        return true;
    }

    public bool AttachModule(Arena arena)
    {
        // Do something specifically for the arena.
        return true;
    }

    public bool DetachModule(Arena arena)
    {
        // Do something specifically for the arena.
        return true;
    }

    public bool Unload(IComponentBroker broker)
    {
        return true;
    }
}

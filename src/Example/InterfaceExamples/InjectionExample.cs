using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Example.InterfaceExamples;

/// <summary>
/// An example on how to inject a component interface dependency into a constructor.
/// </summary>
public sealed class InjectionExample : IModule
{
    private readonly ILogManager _logManager;

    // Here we declare ILogManager as being a required dependency.
    public InjectionExample(ILogManager logManager)
    {
        _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
    }

    bool IModule.Load(IComponentBroker broker)
    {
        // Use it.
        _logManager.LogM(LogLevel.Info, nameof(InjectionExample), "Subspace Server .NET is awesome!");

        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        // For the injected component interfaces, 
        // getting the interface is done for you, and
        // releasing it is too. There's nothing to do.
        return true;
    }
}

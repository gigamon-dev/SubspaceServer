using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Example.InterfaceExamples;

/// <summary>
/// This example shows a manually gotten Component Interface
/// that is used for the entire life of the module.
/// </summary>
public sealed class ManualExample : IModule
{
    private ILogManager? _logManager;

    bool IModule.Load(IComponentBroker broker)
    {
        // You can hold onto the reference,
        // but you must release it at some point.
        _logManager = broker.GetInterface<ILogManager>();

        // Use it, only if it was available.
        // Keep in mind, GetInterface could have returned null.
        // Therefore, checking for null with the ?. (null-conditional operator)
        _logManager?.LogM(LogLevel.Info, nameof(ManualExample), "Subspace Server .NET is awesome!");

        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        // Manually gotten Component Interfaces must be manually released.
        // This is necessary!
        // If we had forgotten to do it, the LogManager module would not Unload.
        if (_logManager is not null)
            broker.ReleaseInterface(ref _logManager);

        return true;
    }
}

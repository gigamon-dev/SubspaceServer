using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Example.InterfaceExamples;

/// <summary>
/// This is an example that shows getting a Component Interface
/// for a short period, using it, and releasing it when done.
/// </summary>
public class ManualExample2(IComponentBroker broker) : IModule
{
    private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));

    bool IModule.Load(IComponentBroker broker)
    {
        LogSomething("Hello Subspace!");
        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        return true;
    }

    public void LogSomething(string message)
    {
        // Try to get it.
        ILogManager? logManager = _broker.GetInterface<ILogManager>();

        // Check whether it was available.
        if (logManager is not null)
        {
            // It was available.
            try
            {
                // Use it.
                logManager.LogM(LogLevel.Info, nameof(ManualExample2), message);
            }
            finally
            {
                // Release it when done.
                _broker.ReleaseInterface(ref logManager);
            }
        }
    }
}

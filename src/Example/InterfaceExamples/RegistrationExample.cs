using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Example.InterfaceExamples;

public interface IMyExample : IComponentInterface
{
    // It's a normal C# interface, so you can include 
    // any members your want to expose to others.
    // These are usually methods, but is not limited to that.
    // For example, the IPlayerData interface has a property
    // to access the collection of Players.

    // Here's an example of exposing a method that we'll use later.
    void DoSomething();
}

public class RegistrationExample : IModule, IMyExample
{
    private InterfaceRegistrationToken<IMyExample>? _iMyExampleToken;

    bool IModule.Load(IComponentBroker broker)
    {
        // Register the interface.
        // This is normally done at the end of Load, when everything is initialized and ready.
        // Notice the return value is a token that we'll later use to unregister.
        _iMyExampleToken = broker.RegisterInterface<IMyExample>(this);

        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        // Unregister the interface.
        // This is normally the first thing done in Unload.
        if (broker.UnregisterInterface(ref _iMyExampleToken) != 0)
            return false;

        // Do other cleanup now that others should no longer be accessing us.
        // ...

        return true;
    }

    // Here the interface is explicitly implemented, 
    // but it doesn't need to be explicit. It's up to you.
    void IMyExample.DoSomething()
    {
        // Do some action here that you wanted exposed to other components.
    }
}

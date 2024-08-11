using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Example.AdvisorExamples;

/// <summary>
/// This is an example on how to register and unregister a custom advisor.
/// </summary>
public class RegistrationExample : IModule, IMyExampleAdvisor
{
    private AdvisorRegistrationToken<IMyExampleAdvisor>? _iMyExampleAdvisorRegistrationToken;

    bool IModule.Load(IComponentBroker broker)
    {
        // Register the module as an implementer
        // of the IMyExampleAdvisor component advisor interface.
        _iMyExampleAdvisorRegistrationToken = broker.RegisterAdvisor<IMyExampleAdvisor>(this);

        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        // Unregister
        broker.UnregisterAdvisor(ref _iMyExampleAdvisorRegistrationToken);

        return true;
    }

    // Notice that this is explicitly implemented, 
    // that's because we defined a default implementation
    // and this is going to override the default.
    bool IMyExampleAdvisor.IsAllowedToDoSomething(Player player)
    {
        // Whatever logic you decide.
        // This has the advisor say it's allowed if the player
        // is currently in a Warbird.
        return player.Ship == ShipType.Warbird;
    }
}

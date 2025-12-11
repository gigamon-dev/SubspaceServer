using SS.Core;
using SS.Core.ComponentInterfaces;

namespace Example.AdvisorExamples;

/// <summary>
/// An example on how to use an advisor.
/// </summary>
/// <param name="broker">The global (zone-wide) broker.</param>
public sealed class UseAdvisorExample(IComponentBroker broker) : IModule
{
    private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));

    bool IModule.Load(IComponentBroker broker)
    {
        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        return true;
    }

    // Make believe this was used at some point
    // in the operation of your module.
    public void DoSomething(Player player)
    {
        bool allow = true;

        // Get the advisors collection.
        var advisors = _broker.GetAdvisors<IMyExampleAdvisor>();

        // Ask each advisor for advice.
        // How you decide to use advice from an advisor is up to you.
        // Here we'll consider something to be allowed, only if
        // every advisor says it's allowed.
        foreach (var advisor in advisors)
        {
            if (!advisor.IsAllowedToDoSomething(player))
            {
                // One advisor said it's not allowed, so we're done.
                // There's no reason to ask other advisors.
                allow = false;
                break;
            }
        }

        if (allow)
        {
            // Do the 'something' that is player is allowed to do.
        }
        else
        {
            // Otherwise, do something else.
        }
    }
}

using SS.Core;

namespace Example.AdvisorExamples;

/// <summary>
/// An example of how to create your own custom advisor.
/// See the <see cref="RegistrationExample"/> for how to register and unregister an implementation.
/// Sse the <see cref="UseAdvisorExample"/> for how to use the advisor.
/// </summary>
public interface IMyExampleAdvisor : IComponentAdvisor
{
    // It can contain any members you want.

    // Here's method that could be used to check 
    // if a player is allowed to do something. 
    // So it has a parameter to pass in the player, 
    // and a bool return value to indicate if the player
    // is allowed.
    //
    // Also, notice that it's possible to include a default
    // implementation. Here, our default implementation
    // returns true by default.
    // 
    // Default implementations are useful if you have many 
    // members and don't want to require implementers to
    // define every member.
    bool IsAllowedToDoSomething(Player player) => false;

    // Here's another example, for a method which 
    // could be used to ask advisors to rate a player.
    // The options are endless, only limited to your imagination.
    int GetRating(Player player) => 10;
}

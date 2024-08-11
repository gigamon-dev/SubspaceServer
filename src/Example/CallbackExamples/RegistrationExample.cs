using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace Example.CallbackExamples;

/// <summary>
/// An example on how to register and unregister a callback on root broker (zone-wide).
/// </summary>
public class RegistrationExample(IChat chat) : IModule
{
    private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));

    public bool Load(IComponentBroker broker)
    {
        // Register on the root broker.
        PlayerActionCallback.Register(broker, Callback_PlayerAction);
        return true;
    }

    public bool Unload(IComponentBroker broker)
    {
        // Unregister on the root broker.
        PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
        return true;
    }

    private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
    {
        if (action == PlayerAction.EnterArena)
        {
            _chat.SendArenaMessage(arena, $"Huzzah! {player.Name} entered the arena!");
        }
        else if (action == PlayerAction.LeaveArena)
        {
            _chat.SendArenaMessage(arena, $"Poof! {player.Name} left!");
        }
    }
}

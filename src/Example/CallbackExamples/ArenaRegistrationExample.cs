using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;

namespace Example.CallbackExamples;

/// <summary>
/// An example on how to register and unregister a callback on an arena.
/// </summary>
/// <param name="chat"></param>
public class ArenaRegistrationExample(IChat chat) : IModule, IArenaAttachableModule
{
    private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));

    bool IModule.Load(IComponentBroker broker)
    {
        return true;
    }

    bool IModule.Unload(IComponentBroker broker)
    {
        return true;
    }

    bool IArenaAttachableModule.AttachModule(Arena arena)
    {
        // Register on the arena.
        PlayerActionCallback.Register(arena, Callback_PlayerAction);

        return true;
    }

    bool IArenaAttachableModule.DetachModule(Arena arena)
    {
        // Unregister on the arena.
        PlayerActionCallback.Unregister(arena, Callback_PlayerAction);

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

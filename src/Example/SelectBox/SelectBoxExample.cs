using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities.ObjectPool;
using System.Buffers;

namespace Example.SelectBox;

/// <summary>
/// This is an example of how to use the <see cref="SS.Core.Modules.SelectBox"/> module.
/// This example opens the select box when a player enters an arena.
/// Selecting an choice, prints the choice to the player in a green arena chat message.
/// </summary>
/// <remarks>
/// <para>
/// This example shows everything written for high performance.
/// It uses object pools for object reuse, rather than allocate objects that will be used once.
/// </para>
/// <para>
/// To run the example, add this module to the conf/Modules.conf file.
/// The Module.conf included with the server has it commented out. Umcomment it and run server.
/// </para>
/// </remarks>
public sealed class SelectBoxExample(
    IChat chat,
    IPrng prng,
    ISelectBox selectBox) : IModule
{
    private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
    private readonly IPrng _prng = prng ?? throw new ArgumentNullException(nameof(prng));
    private readonly ISelectBox _selectBox = selectBox ?? throw new ArgumentNullException(nameof(selectBox));

    private static readonly ObjectPool<List<SelectBoxItem>> s_selectBoxItemListPool = ObjectPool.Create(new ListPooledObjectPolicy<SelectBoxItem>());

    public bool Load(IComponentBroker broker)
    {
        PlayerActionCallback.Register(broker, Callback_PlayerAction);
        SelectBoxItemSelectedCallback.Register(broker, Callback_SelectBoxItemSelected);
        return true;
    }

    public bool Unload(IComponentBroker broker)
    {
        PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
        SelectBoxItemSelectedCallback.Unregister(broker, Callback_SelectBoxItemSelected);
        return true;
    }

    private void Callback_PlayerAction(Player player, PlayerAction action, Arena? arena)
    {
        if (action == PlayerAction.EnterGame)
        {
            // Create the title.
            // Let's dynamically create the title with the player's name.
            // To avoid allocating a string object, use stackalloc.
            const string WelcomeText = "Welcome to the select box example ";
            Span<char> title = stackalloc char[WelcomeText.Length + player.Name!.Length];
            if (!title.TryWrite($"{WelcomeText}{player.Name}", out int charsWritten) || charsWritten != title.Length)
                return;

            const int MinItems = 5;
            const int MaxItems = 10;
            const int MaxItemLength = 40;

            // Get a random number for how many items to display.
            int itemCount = _prng.Number(MinItems, MaxItems);

            // To avoid allocating string objects for each item in the list, we'll use a rented character array.
            char[] buffer = ArrayPool<char>.Shared.Rent(MaxItems * MaxItemLength);

            // To avoid allocating a new list every time, use a pool.
            List<SelectBoxItem> items = s_selectBoxItemListPool.Get();

            try
            {
                Memory<char> remaining = buffer;

                for (short i = 1; i <= itemCount; i++)
                {
                    // The easiest way is to just allocate a string.
                    // This is how that could be done:
                    //items.Add((i, $"This is choice #{i} in the select box.".AsMemory()));

                    // However, we want to avoid allocating memory.
                    // So, we're using a rented array instead.
                    // Slice off pieces of the array for each item.
                    Memory<char> text = remaining[..MaxItemLength];
                    remaining = remaining[MaxItemLength..];

                    if (!text.Span.TryWrite($"This is choice #{i} in the select box.", out charsWritten) || charsWritten <= 0)
                        continue;

                    // Add the item.
                    items.Add(new SelectBoxItem(i, text[..charsWritten]));
                }

                // Display the select box to the player.
                _selectBox.Open(player, title, items);
            }
            finally
            {
                // Return the array back to the pool.
                ArrayPool<char>.Shared.Return(buffer, true);

                // Return the list to the pool.
                s_selectBoxItemListPool.Return(items);
            }
        }
    }

    private void Callback_SelectBoxItemSelected(Player player, short itemValue, ReadOnlySpan<char> itemText)
    {
        _chat.SendMessage(player, $"You selected option {itemValue} - {itemText}");
    }
}

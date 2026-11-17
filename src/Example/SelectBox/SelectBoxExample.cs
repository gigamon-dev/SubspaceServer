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
/// If the player doesn't choose within 30 seconds, the select box expires.
/// </summary>
/// <remarks>
/// <para>
/// This example shows everything written for high performance.
/// It uses object pools for object reuse, rather than allocate objects that will be used once.
/// </para>
/// <para>
/// A state object is passed when opening the select box. It is passed back when the player chooses an item.
/// Once the select box is completed (no longer associated with any player), the state object is returned to its pool.
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
    private static readonly ObjectPool<MenuState> s_menuStatePool = ObjectPool.Create<MenuState>();

    private static readonly TimeSpan MenuTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The select boxes that aren't completed yet, so that they can be cancelled when the module is unloaded.
    /// </summary>
    private readonly HashSet<MenuState> _openMenus = [];

    // The delegates are created once, rather than each time a select box is opened.
    private SelectBoxItemSelectedDelegate<MenuState>? _menuSelected;
    private SelectBoxCompletedDelegate<MenuState>? _menuCompleted;

    public bool Load(IComponentBroker broker)
    {
        _menuSelected = Menu_Selected;
        _menuCompleted = Menu_Completed;
        PlayerActionCallback.Register(broker, Callback_PlayerAction);
        return true;
    }

    public bool Unload(IComponentBroker broker)
    {
        PlayerActionCallback.Unregister(broker, Callback_PlayerAction);

        // Cancel the select boxes that aren't completed yet, so that the delegates aren't called after the module is unloaded.
        // Cancelling calls the completed delegate, which removes it from the set. So, loop over a copy.
        foreach (MenuState menu in new List<MenuState>(_openMenus))
        {
            _selectBox.Cancel(menu.Handle);
        }

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

                // The state object to pass back to the delegates.
                MenuState menu = s_menuStatePool.Get();
                menu.ItemCount = items.Count;
                _openMenus.Add(menu);

                // Display the select box to the player.
                SelectBoxHandle handle = _selectBox.Open(player, title, items, menu, _menuSelected!, _menuCompleted, MenuTimeout);

                // If the select box wasn't displayed (e.g., the player's client doesn't support it), it's already completed.
                if (_openMenus.Contains(menu))
                    menu.Handle = handle;
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

    private void Menu_Selected(Player player, short itemValue, ReadOnlySpan<char> itemText, MenuState menu)
    {
        _chat.SendMessage(player, $"You selected option {itemValue} of {menu.ItemCount} - {itemText}");
    }

    private void Menu_Completed(MenuState menu)
    {
        // The select box no longer references the state object, so it can be reused.
        _openMenus.Remove(menu);
        s_menuStatePool.Return(menu);
    }

    private sealed class MenuState : IResettable
    {
        public SelectBoxHandle Handle;
        public int ItemCount;

        public bool TryReset()
        {
            Handle = default;
            ItemCount = 0;
            return true;
        }
    }
}

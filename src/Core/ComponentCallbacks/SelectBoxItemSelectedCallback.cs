using System;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a player selects an item from the select box UI, or runs the ?select command manually.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="itemValue">The value of the item.</param>
    /// <param name="itemText">The text of the item.</param>
    [ComponentCallback]
    public delegate void SelectBoxItemSelectedCallback(Player player, short itemValue, ReadOnlySpan<char> itemText);
}

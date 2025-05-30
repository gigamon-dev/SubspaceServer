using System;
using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// An item for displaying in a select box.
    /// </summary>
    /// <param name="Value">The value of the item.</param>
    /// <param name="Text">The text to display to the player.</param>
    public readonly record struct SelectBoxItem(short Value, ReadOnlyMemory<char> Text);

    /// <summary>
    /// Interface for a service for displaying a UI for player(s) to choose from a list of options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Continuum pops up a select box, similar to the arena selection box.
    /// The user can either choose one of items, or hit the ESC key to close it.
    /// If the user chooses an item, it executes the ?select command.
    /// Register a <see cref="ComponentCallbacks.SelectBoxItemSelectedCallback"/> handler to process the choice.
    /// If the user closes the select box without choosing an item (hitting the ESC key), the server cannot tell, since there is no command executed.
    /// Also, the server is able to tell the client to display the select box, but it is unable to hide it.
    /// </para>
    /// <para>
    /// Be aware of the following limitations:
    /// <list type="bullet">
    /// <item>The packet for the select box is limited to <see cref="Modules.SelectBox.MaxSelectPacketLength"/></item>
    /// <item>The title is limited to <see cref="Modules.SelectBox.MaxTitleLength"/> ^</item>
    /// <item>Each item's text is limited to <see cref="Modules.SelectBox.MaxItemTextLength"/> ^</item>
    /// </list>
    /// ^ The Subspace protocol uses a single-byte encoding. So, the limit in bytes equals the number of characters allowed (-1 for the required null-terminator).
    /// <para>
    /// If given a string value longer than can fit into a field, it will truncate the string to fit.
    /// If there are too many items, it will fit as many as possible, until an item doesn't fit.
    /// </para>
    /// </para>
    /// </remarks>
    public interface ISelectBox : IComponentInterface
    {
        /// <summary>
        /// Displays the select box user interface to player(s).
        /// </summary>
        /// <param name="target">A target representing which player(s) to display the select box to.</param>
        /// <param name="title">The title.</param>
        /// <param name="items">The items. The values of items are usually unique, but are not required to be.</param>
        void Open(ITarget target, ReadOnlySpan<char> title, IReadOnlyList<SelectBoxItem> items);
    }
}

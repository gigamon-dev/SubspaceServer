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
    /// Delegate for when a player selects an item from a select box.
    /// </summary>
    /// <typeparam name="TState">The type of the state object.</typeparam>
    /// <param name="player">The player that selected the item.</param>
    /// <param name="itemValue">The value of the item.</param>
    /// <param name="itemText">The text of the item.</param>
    /// <param name="state">The state object that was passed when the select box was opened.</param>
    public delegate void SelectBoxItemSelectedDelegate<TState>(Player player, short itemValue, ReadOnlySpan<char> itemText, TState state);

    /// <summary>
    /// Delegate for when a select box is no longer open for any player.
    /// </summary>
    /// <typeparam name="TState">The type of the state object.</typeparam>
    /// <param name="state">The state object that was passed when the select box was opened. It is no longer referenced by the select box, and can be reused.</param>
    public delegate void SelectBoxCompletedDelegate<TState>(TState state);

    /// <summary>
    /// Identifies a select box that was opened with <see cref="ISelectBox.Open"/>, for cancelling it with <see cref="ISelectBox.Cancel(SelectBoxHandle)"/>.
    /// </summary>
    /// <remarks>
    /// A handle only refers to the select box it was returned for.
    /// Once that select box is completed, the handle no longer refers to anything, and cancelling it does nothing.
    /// </remarks>
    public readonly struct SelectBoxHandle : IEquatable<SelectBoxHandle>
    {
        internal SelectBoxHandle(object session, int version)
        {
            Session = session;
            Version = version;
        }

        internal object? Session { get; }

        internal int Version { get; }

        /// <summary>
        /// Whether the handle does not refer to a select box (the select box was not opened for anyone).
        /// </summary>
        public bool IsEmpty => Session is null;

        public bool Equals(SelectBoxHandle other) => ReferenceEquals(Session, other.Session) && Version == other.Version;

        public override bool Equals(object? obj) => obj is SelectBoxHandle other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Session, Version);

        public static bool operator ==(SelectBoxHandle left, SelectBoxHandle right) => left.Equals(right);

        public static bool operator !=(SelectBoxHandle left, SelectBoxHandle right) => !left.Equals(right);
    }

    /// <summary>
    /// Interface for a service for displaying a UI for player(s) to choose from a list of options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Continuum pops up a select box, similar to the arena selection box.
    /// The player can choose one of the items (ENTER, while the chat line is empty), or close it (ESC).
    /// Choosing an item closes the select box, and sends the ?select command with the item's value and text.
    /// Closing it sends nothing, so the server cannot tell that it was closed.
    /// The server is able to tell the client to display a select box, but it is unable to hide it.
    /// A client only has one select box, so opening one replaces the one that was open.
    /// The select box stays open when the player changes arenas.
    /// </para>
    /// <para>
    /// For each player, this service keeps track of the select box that was last opened for the player.
    /// A player is no longer associated with a select box when:
    /// <list type="bullet">
    /// <item>the player selects one of its items,</item>
    /// <item>another select box is opened for the player,</item>
    /// <item>the select box times out (if it was opened with a timeout),</item>
    /// <item>the select box is cancelled,</item>
    /// <item>or the player leaves the arena (or disconnects).</item>
    /// </list>
    /// A ?select that doesn't match an item (value and text) of the select box that the player is associated with is ignored.
    /// Once no players are associated with a select box, it is completed: its completed delegate is called, so that the state object can be reused.
    /// </para>
    /// <para>
    /// When a select box times out or is cancelled, the players still associated with it are sent a replacement select box,
    /// with the same title and a single item, <see cref="Modules.SelectBox.ExpiredItemText"/>, which is ignored if selected.
    /// Players that already closed the select box (ESC) will see it open again.
    /// When a player leaves the arena, nothing is sent, so the select box may still be displayed in the new arena, and selecting an item from it is ignored.
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
    /// <para>
    /// The methods of this interface must be called on the mainloop thread. The delegates are called on the mainloop thread.
    /// A module that opens select boxes should cancel the ones it opened (that aren't completed yet) when it unloads,
    /// so that its delegates aren't called afterwards.
    /// </para>
    /// </remarks>
    public interface ISelectBox : IComponentInterface
    {
        /// <summary>
        /// Displays a select box to player(s).
        /// </summary>
        /// <typeparam name="TState">The type of the state object.</typeparam>
        /// <param name="target">A target representing which player(s) to display the select box to. Only players in an arena, using a client that supports select boxes, are included.</param>
        /// <param name="title">The title.</param>
        /// <param name="items">The items. The values of items are usually unique, but are not required to be.</param>
        /// <param name="state">A state object to pass to the delegates.</param>
        /// <param name="selected">Called when a player selects an item.</param>
        /// <param name="completed">
        /// Called once the select box is no longer associated with any player.
        /// It is called exactly once for each call to this method, including when the select box isn't displayed to anyone (immediately, in that case).
        /// </param>
        /// <param name="timeout">
        /// How long to wait for players to select an item, or <see langword="null"/> to wait until the player selects an item or is no longer associated with the select box for another reason.
        /// </param>
        /// <returns>A handle for cancelling the select box. It is empty if the select box wasn't displayed to anyone.</returns>
        SelectBoxHandle Open<TState>(
            ITarget target,
            ReadOnlySpan<char> title,
            IReadOnlyList<SelectBoxItem> items,
            TState state,
            SelectBoxItemSelectedDelegate<TState> selected,
            SelectBoxCompletedDelegate<TState>? completed = null,
            TimeSpan? timeout = null);

        /// <summary>
        /// Cancels a select box.
        /// The players still associated with it are sent a replacement select box (see <see cref="Modules.SelectBox.ExpiredItemText"/>), and it is completed.
        /// </summary>
        /// <param name="handle">The handle that was returned when the select box was opened.</param>
        /// <returns><see langword="true"/> if the select box was cancelled. <see langword="false"/> if the handle is empty, or the select box was already completed.</returns>
        bool Cancel(SelectBoxHandle handle);
    }
}

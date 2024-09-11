using SS.Packets.Game;
using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that controls lvz objects.
    /// </summary>
    /// <remarks>
    /// LVZ objects can be toggled or changed.
    /// The majority of use should be to toggle objects on and off.
    /// Changing objects is more advanced, but also uses more network bandwidth.
    /// When designing LVZ files is recommended to use toggling when possible.
    /// Toggling objects uses: 1 + 2 * &lt;# object changed&gt; bytes.
    /// Changing objects uses: 1 + 11 * &lt;# object changed&gt; bytes.
    /// In other words, rather than change one object's image (12 bytes), it's better to toggle one object off and a another object on (5 bytes).
    /// </remarks>
    public interface ILvzObjects : IComponentInterface
    {
        /// <summary>
        /// Sends the full, current state of lvz objects to a <paramref name="player"/> for the arena the player is in.
        /// </summary>
        /// <remarks>
        /// When objects are toggled or changed with an <see cref="Arena"/> as the <see cref="ITarget"/>, the changes are recorded for the arena.
        /// This method will send all the recorded changes for the arena to the <paramref name="player"/>.
        /// <para>
        /// This method is automatically called when a player fully enters an arena (<see cref="PlayerAction.EnterGame"/>).
        /// It is unlikely that other modules will need to call this method. It is available just in case.
        /// </para>
        /// </remarks>
        /// <param name="player">The player to send the lvz data to.</param>
        void SendState(Player player);

        #region Toggle methods

        /// <summary>
        /// Toggles a single lvz object.
        /// </summary>
        /// <remarks>
        /// When toggling multiple objects, it's recommended to use <see cref="Toggle(ITarget, ReadOnlySpan{LvzObjectToggle})"/> instead, to reduce the amount of network bandwidth used.
        /// </remarks>
        /// <param name="target">
        /// The target representing which player(s) to send the change to.
        /// When an <see cref="Arena"/> is the target, the changes are additionally recorded for the arena and automatically sent to players that fully enter the arena (<see cref="PlayerAction.EnterGame"/>).
        /// </param>
        /// <param name="id">The Id of the lvz object to toggle.</param>
        /// <param name="isEnabled"><see langword="true"/> to enable the object. <see langword="false"/> to disable the object.</param>
        void Toggle(ITarget target, short id, bool isEnabled);

        /// <summary>
        /// Toggles one or more lvz objects.
        /// </summary>
        /// <remarks>
        /// When toggling multiple objects, using this method is preferred since it can combine multiple toggles into the same packet, reducing the amount of network bandwidth used.
        /// </remarks>
        /// <param name="target">
        /// The target representing which player(s) to send the change to.
        /// When an <see cref="Arena"/> is the target, the changes are additionally recorded for the arena and automatically sent to players that fully enter the arena (<see cref="PlayerAction.EnterGame"/>).
        /// </param>
        /// <param name="toggles">The toggles to apply.</param>
        void Toggle(ITarget target, ReadOnlySpan<LvzObjectToggle> toggles);

        #endregion

        #region Change methods

        /// <summary>
        /// Sets the position a lvz object.
        /// </summary>
        /// <remarks>
        /// This method performs a single change to a single lvz object.
        /// When making multiple changes to a single lvz object or when changing multiple lvz objects, 
        /// it's recommended to use <see cref="Set(ITarget, ReadOnlySpan{LvzObjectChange})"/> instead, 
        /// to reduce the amount of network bandwidth used.
        /// </remarks>
        /// <param name="target">
        /// The target representing which player(s) to send the change to.
        /// When an <see cref="Arena"/> is the target, the changes are additionally recorded for the arena and automatically sent to players that fully enter the arena (<see cref="PlayerAction.EnterGame"/>).
        /// </param>
        /// <param name="id">The Id of the lvz object to change.</param>
        /// <param name="x">The x-coordinate to set.</param>
        /// <param name="y">The y-coordinate to set.</param>
        /// <param name="offsetX">The x-offset to set (screen objects only).</param>
        /// <param name="offsetY">The y-offset to set (screen objects only).</param>
        void SetPosition(ITarget target, short id, short x, short y, ScreenOffset offsetX, ScreenOffset offsetY);

        /// <summary>
        /// Sets the image of a lvz object.
        /// </summary>
        /// <remarks>
        /// This method performs a single change to a single lvz object.
        /// When making multiple changes to a single lvz object or when changing multiple lvz objects, 
        /// it's recommended to use <see cref="Set(ITarget, ReadOnlySpan{LvzObjectChange})"/> instead, 
        /// to reduce the amount of network bandwidth used.
        /// </remarks>
        /// <param name="target">
        /// The target representing which player(s) to send the change to.
        /// When an <see cref="Arena"/> is the target, the changes are additionally recorded for the arena and automatically sent to players that fully enter the arena (<see cref="PlayerAction.EnterGame"/>).
        /// </param>
        /// <param name="id">The Id of the lvz object to change.</param>
        /// <param name="imageId">The Id of the image to set.</param>
        void SetImage(ITarget target, short id, byte imageId);

        /// <summary>
        /// Sets the display layer of a lvz object.
        /// </summary>
        /// <remarks>
        /// This method performs a single change to a single lvz object.
        /// When making multiple changes to a single lvz object or when changing multiple lvz objects, 
        /// it's recommended to use <see cref="Set(ITarget, ReadOnlySpan{LvzObjectChange})"/> instead, 
        /// to reduce the amount of network bandwidth used.
        /// </remarks>
        /// <param name="target">
        /// The target representing which player(s) to send the change to.
        /// When an <see cref="Arena"/> is the target, the changes are additionally recorded for the arena and automatically sent to players that fully enter the arena (<see cref="PlayerAction.EnterGame"/>).
        /// </param>
        /// <param name="id">The Id of the lvz object to change.</param>
        /// <param name="layer">The display layer to set.</param>
        void SetLayer(ITarget target, short id, DisplayLayer layer);

        /// <summary>
        /// Sets the timer of a lvz object.
        /// </summary>
        /// <remarks>
        /// This method performs a single change to a single lvz object.
        /// When making multiple changes to a single lvz object or when changing multiple lvz objects, 
        /// it's recommended to use <see cref="Set(ITarget, ReadOnlySpan{LvzObjectChange})"/> instead, 
        /// to reduce the amount of network bandwidth used.
        /// </remarks>
        /// <param name="target">
        /// The target representing which player(s) to send the change to.
        /// When an <see cref="Arena"/> is the target, the changes are additionally recorded for the arena and automatically sent to players that fully enter the arena (<see cref="PlayerAction.EnterGame"/>).
        /// </param>
        /// <param name="id">The Id of the lvz object to change.</param>
        /// <param name="time">The time to set.</param>
        void SetTimer(ITarget target, short id, ushort time);

        /// <summary>
        /// Sets the display mode of a lvz object.
        /// </summary>
        /// <remarks>
        /// This method performs a single change to a single lvz object.
        /// When making multiple changes to a single lvz object or when changing multiple lvz objects, 
        /// it's recommended to use <see cref="Set(ITarget, ReadOnlySpan{LvzObjectChange})"/> instead, 
        /// to reduce the amount of network bandwidth used.
        /// </remarks>
        /// <param name="target">
        /// The target representing which player(s) to send the change to.
        /// When an <see cref="Arena"/> is the target, the changes are additionally recorded for the arena and automatically sent to players that fully enter the arena (<see cref="PlayerAction.EnterGame"/>).
        /// </param>
        /// <param name="id">The Id of the lvz object to change.</param>
        /// <param name="mode">The display mode to set.</param>
        void SetMode(ITarget target, short id, DisplayMode mode);

        /// <summary>
        /// Changes one or more lvz objects.
        /// </summary>
        /// <remarks>
        /// This allows changing multiple aspects (position, image, layer, timer, mode) of an object at once.
        /// <para>
        /// Use <see cref="TryGetDefaultInfo(Arena, short, out bool, out ObjectData)"/> to get a copy of the original object(s).
        /// </para>
        /// <para>
        /// Using this method has the benefit of consolidating the data to send into as few packets as possible.
        /// </para>
        /// </remarks>
        /// <param name="target">
        /// The target representing which player(s) to send the change to.
        /// When an <see cref="Arena"/> is the target, the changes are additionally recorded for the arena and automatically sent to players that fully enter the arena (<see cref="PlayerAction.EnterGame"/>).
        /// </param>
        /// <param name="changes">The changes to apply.</param>
        void Set(ITarget target, ReadOnlySpan<LvzObjectChange> changes);

        #endregion

        /// <summary>
        /// Changes and/or toggles one or more lvz objects.
        /// </summary>
        /// <remarks>
        /// This is a convenience method to call both <see cref="Toggle(ITarget, ReadOnlySpan{LvzObjectToggle})"/> and <see cref="Set(ITarget, ReadOnlySpan{LvzObjectChange})"/>.
        /// It's useful since when making changes to lvz objects, it is highly likely the objects need to be toggled as well.
        /// </remarks>
        /// <param name="target">
        /// The target representing which player(s) to send the change to.
        /// When an <see cref="Arena"/> is the target, the changes are additionally recorded for the arena and automatically sent to players that fully enter the arena (<see cref="PlayerAction.EnterGame"/>).
        /// </param>
        /// <param name="changes">The changes to apply.</param>
        /// <param name="toggles">The toggles to apply.</param>
        void SetAndToggle(ITarget target, ReadOnlySpan<LvzObjectChange> changes, ReadOnlySpan<LvzObjectToggle> toggles);

        #region Reset

        /// <summary>
        /// Resets an lvz object's arena-level state back to it's default state.
        /// </summary>
        /// <param name="arena">The arena to reset the lvz object in.</param>
        /// <param name="id">The Id of the lvz object to reset.</param>
        /// <param name="sendChanges">
        /// <see langword="true"/> to send lvz change packets and lvz toggle packets.
        /// <see langword="false"/> to only send lvz toggle packets (recommended to reduce network bandwidth use).
        /// </param>
        void Reset(Arena arena, short id, bool sendChanges = false);

        /// <summary>
        /// Resets all lvz objects' arena-level state back to their default state.
        /// </summary>
        /// <param name="arena">The arena to reset the lvz objects in.</param>
        /// <param name="sendChanges">
        /// <see langword="true"/> to send lvz change packets and lvz toggle packets.
        /// <see langword="false"/> to only send lvz toggle packets (recommended to reduce network bandwidth use).
        /// </param>
        void Reset(Arena arena, bool sendChanges = false);

        #endregion

        #region Info methods

        /// <summary>
        /// Gets the default info of a lvz object.
        /// </summary>
        /// <remarks>
        /// This is the object's original state. It does not include any changes made to it.
        /// </remarks>
        /// <param name="arena">The arena to get lvz info for.</param>
        /// <param name="id">The Id of the lvz object to get info about.</param>
        /// <param name="isEnabled">Whether the object is enabled.</param>
        /// <param name="objectData">When this method returns, the info if the object was found.</param>
        /// <returns><see langword="true"/> if the object was found. Otherwise <see langword="false"/>.</returns>
        bool TryGetDefaultInfo(Arena arena, short id, out bool isEnabled, out ObjectData objectData);

        /// <summary>
        /// Gets the current info of a lvz object.
        /// </summary>
        /// <remarks>
        /// This is the object's latest info, including any changes made to it.
        /// </remarks>
        /// <param name="arena">The arena to get lvz info for.</param>
        /// <param name="id">The Id of the lvz object to get info about.</param>
        /// <param name="isEnabled">Whether the object is enabled.</param>
        /// <param name="objectData">When this method returns, the info if the object was found.</param>
        /// <returns><see langword="true"/> if the object was found. Otherwise <see langword="false"/>.</returns>
        bool TryGetCurrentInfo(Arena arena, short id, out bool isEnabled, out ObjectData objectData);

        #endregion
    }
}

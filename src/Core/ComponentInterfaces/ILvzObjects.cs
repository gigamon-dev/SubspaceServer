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
        /// Sends the full, current state of lvz objects to a player for the arena the player is in.
        /// </summary>
        /// <param name="player">The player to send the lvz data to.</param>
        void SendState(Player player);

        #region Toggle methods

        /// <summary>
        /// Toggles a single lvz object.
        /// </summary>
        /// <remarks>
        /// When toggling multiple objects, it's recommended to call <see cref="ToggleSet(ITarget, ReadOnlySpan{LvzObjectToggle})"/>
        /// to reduce the amount of data being send over the network.
        /// <see cref="ToggleSet(ITarget, ReadOnlySpan{LvzObjectToggle})"/> can group multiple toggles into a single packet.
        /// </remarks>
        /// <param name="target">The target representing which player(s) to send the change to.</param>
        /// <param name="id">The Id of the lvz object to toggle.</param>
        /// <param name="isEnabled"><see langword="true"/> to enable the object. <see langword="false"/> to disable the object.</param>
        void Toggle(ITarget target, short id, bool isEnabled);

        /// <summary>
        /// Toggles a set of lvz objects.
        /// </summary>
        /// <remarks>
        /// The lvz object changes are sent together.
        /// </remarks>
        /// <param name="target">The target representing which player(s) to send the change to.</param>
        /// <param name="set">The collection of changes to make.</param>
        void ToggleSet(ITarget target, ReadOnlySpan<LvzObjectToggle> set);

        #endregion

        #region Change methods

        /// <summary>
        /// Sets the position a lvz object.
        /// </summary>
        /// <param name="target">The target representing which player(s) to send the change to.</param>
        /// <param name="id">The Id of the lvz object to change.</param>
        /// <param name="x">The x-coordinate to set.</param>
        /// <param name="y">The y-coordinate to set.</param>
        /// <param name="offsetX">The x-offset to set (screen objects only).</param>
        /// <param name="offsetY">The y-offset to set (screen objects only).</param>
        void SetPosition(ITarget target, short id, short x, short y, ScreenOffset offsetX, ScreenOffset offsetY);

        /// <summary>
        /// Sets the image of a lvz object.
        /// </summary>
        /// <param name="target">The target representing which player(s) to send the change to.</param>
        /// <param name="id">The Id of the lvz object to change.</param>
        /// <param name="imageId">The Id of the image to set.</param>
        void SetImage(ITarget target, short id, byte imageId);

        /// <summary>
        /// Sets the display layer of a lvz object.
        /// </summary>
        /// <param name="target">The target representing which player(s) to send the change to.</param>
        /// <param name="id">The Id of the lvz object to change.</param>
        /// <param name="layer">The display layer to set.</param>
        void SetLayer(ITarget target, short id, DisplayLayer layer);

        /// <summary>
        /// Sets the timer of a lvz object.
        /// </summary>
        /// <param name="target">The target representing which player(s) to send the change to.</param>
        /// <param name="id">The Id of the lvz object to change.</param>
        /// <param name="time">The time to set.</param>
        void SetTimer(ITarget target, short id, ushort time);

        /// <summary>
        /// Sets the display mode of a lvz object.
        /// </summary>
        /// <param name="target">The target representing which player(s) to send the change to.</param>
        /// <param name="id">The Id of the lvz object to change.</param>
        /// <param name="mode">The display mode to set.</param>
        void SetMode(ITarget target, short id, DisplayMode mode);

        #endregion

        /// <summary>
        /// Resets a lvz object back to it's default state.
        /// </summary>
        /// <remarks>
        /// If the object was modified, this does not send the change to the players.
        /// However, it does send the toggle object off.
        /// </remarks>
        /// <param name="arena">The arena to reset the lvz object in.</param>
        /// <param name="id">The Id of the lvz object to reset.</param>
        void Reset(Arena arena, short id);

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

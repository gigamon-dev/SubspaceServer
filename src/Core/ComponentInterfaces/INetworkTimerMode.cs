namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for controlling Continuum's timer mode.
    /// </summary>
    /// <remarks>
    /// Continuum includes the ability to use a timer mode which handles when the server tick count wraps.
    /// The feature is toggled by sending the 0x00 0x14 packet.
    /// <para>
    /// This interface provides access to it just for the sake of completeness.
    /// It is likely an experimental, never fully completed feature.
    /// According to developers in the community, it fixes the wrapping problem, 
    /// but isn't a complete fix because Continuum can still send a security violation from its own latency detection being too high client side.
    /// </para>
    /// <para>
    /// The feature can be configured using the <c>Net:UseAlternateTimerMode</c> global.conf setting.
    /// It can also be toggled globally or by player using the <see cref="Modules.SubgameCompatibility"/> module's <c>*tmode</c> command.
    /// </para>
    /// </remarks>
    internal interface INetworkTimerMode : IComponentInterface
    {
        /// <summary>
        /// Gets the timer mode that the server is using for new player connections.
        /// </summary>
        /// <returns>
        /// Whether the alternate timer mode is enabled.
        /// <see langword="false"/> if using the standard timer mode.
        /// <see langword="true"/> if using the alternate timer mode.
        /// </returns>
        public bool GetTimerMode();

        /// <summary>
        /// Sets the timer mode that the server is using for new player connections
        /// and sends the packet to toggle it on all eligible players (Continuum only).
        /// </summary>
        /// <param name="useAlternate">Whether to enable the alternate timer mode.</param>
        public void SetTimerMode(bool useAlternate);

        /// <summary>
        /// Gets a <paramref name="player"/>'s timer mode.
        /// </summary>
        /// <param name="player">The player to get the timer mode of.</param>
        /// <returns>
        /// Whether the alternate timer mode is enabled.
        /// <see langword="false"/> if using the standard timer mode.
        /// <see langword="true"/> if using the alternate timer mode.
        /// </returns>
        public bool GetTimerMode(Player player);

        /// <summary>
        /// Sets a <paramref name="player"/>'s timer mode.
        /// </summary>
        /// <param name="player">The player to set the timer mode of.</param>
        /// <param name="useAlternate">Whether to enable the alternate timer mode.</param>
        public void SetTimerMode(Player player, bool useAlternate);
    }
}

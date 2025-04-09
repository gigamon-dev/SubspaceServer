using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that keeps track of which players are idle and which players are marked available/not available.
    /// </summary>
    public interface IIdle : IComponentInterface
    {
        /// <summary>
        /// Gets how long a player has been idle.
        /// </summary>
        /// <param name="player">The player to get info for.</param>
        /// <returns>The amount of time the  player has been idle.</returns>
        TimeSpan GetIdle(Player player);

        /// <summary>
        /// Sets the player's last active time to be the current time.
        /// </summary>
        /// <param name="player">The player to set info for.</param>
        void ResetIdle(Player player);

        /// <summary>
        /// Gets whether a player is marked as Available.
        /// </summary>
        /// <remarks>
        /// Marking is done using the ?available and ?notavailable commands.
        /// </remarks>
        /// <param name="player">The player to get info for.</param>
        /// <returns><see langword="true"/> if the player is marked Available; otherwise, <see langword="false"/>.</returns>
        bool IsAvailable(Player player);
    }
}

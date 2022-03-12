using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that manages timers for arenas.
    /// </summary>
    public interface IGameTimer : IComponentInterface
    {
        /// <summary>
        /// Sets the arena timer's duration.
        /// </summary>
        /// <remarks>
        /// This can only be used for arenas that are not using a Misc:TimedGame.
        /// </remarks>
        /// <param name="arena">The arena of the timer to set.</param>
        /// <param name="duration">The duration to set. A positive value [re]starts the timer. Otherwise, the timer is stopped.</param>
        /// <returns><see langword="true"/> if the timer was changed (started or stopped). Otherwise <see langword="false"/> for arenas that are using a Misc:TimedGame.</returns>
        bool SetTimer(Arena arena, TimeSpan duration);
    }
}

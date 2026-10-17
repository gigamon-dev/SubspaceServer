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
        /// <para>
        /// This can only be used for arenas that are not using a Misc:TimedGame.
        /// </para>
        /// <para>
        /// When called from a <see cref="ComponentCallbacks.GameTimerEndedCallback"/> handler, a positive duration
        /// continues the game with that much more time (e.g. overtime), and a zero duration leaves the game running
        /// without a timer (e.g. sudden death), for the handler to end itself. In both cases the timer does not
        /// announce the game as over.
        /// </para>
        /// </remarks>
        /// <param name="arena">The arena of the timer to set.</param>
        /// <param name="duration">The duration to set. A positive value [re]starts the timer. Otherwise, the timer is stopped.</param>
        /// <returns><see langword="true"/> if the timer was changed (started or stopped). Otherwise <see langword="false"/> for arenas that are using a Misc:TimedGame.</returns>
        bool SetTimer(Arena arena, TimeSpan duration);

        /// <summary>
        /// Gets the time left on the arena timer.
        /// </summary>
        /// <param name="arena">The arena of the timer.</param>
        /// <param name="remaining">The time left, when the timer is running or paused. Otherwise, <see cref="TimeSpan.Zero"/>.</param>
        /// <returns><see langword="true"/> if the timer is running or paused. Otherwise, <see langword="false"/>.</returns>
        bool TryGetRemaining(Arena arena, out TimeSpan remaining);
    }
}

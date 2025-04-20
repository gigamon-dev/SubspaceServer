using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a module that manages periodic rewards.
    /// </summary>
    public interface IPeriodicReward : IComponentInterface
    {
        /// <summary>
        /// Rewards teams in an arena as if the periodic timer elapsed.
        /// </summary>
        /// <param name="arena">The arena to reward teams in.</param>
        void Reward(Arena arena);

        /// <summary>
        /// Resets the periodic timer in an arena.
        /// </summary>
        /// <param name="arena">The arena to reset the timer in.</param>
        void Reset(Arena arena);

        /// <summary>
        /// Stops the periodic timer in an arena.
        /// </summary>
        /// <remarks>
        /// To restart the timer, use <see cref="Reset(Arena)"/>.
        /// </remarks>
        /// <param name="arena">The arena to stop the timer in.</param>
        void Stop(Arena arena);
    }

    /// <summary>
    /// Interface for providing a custom points implementation for periodic rewards.
    /// </summary>
    public interface IPeriodicRewardPoints : IComponentInterface
    {
        /// <summary>
        /// Basic settings for periodic rewards.
        /// An implementation may choose to read settings from this, or settings of its own.
        /// </summary>
        public interface ISettings
        {
            /// <summary>
            /// The minimum players necessary in the arena to give out periodic rewards.
            /// </summary>
            /// <remarks>
            /// Periodic:RewardMinimumPlayers
            /// </remarks>
            int RewardMinimumPlayers { get; }

            /// <summary>
            /// For determining how many points to award.
            /// </summary>
            /// <remarks>
            /// Periodic:RewardPoints
            /// </remarks>
            int RewardPoints { get; }

            /// <summary>
            /// Whether points are divided among players on a team.
            /// </summary>
            /// <remarks>
            /// Periodic:SplitPoints
            /// </remarks>
            bool SplitPoints { get; }

            /// <summary>
            /// Whether frequencies with zero points will still get a reward notification during the ding.
            /// </summary>
            /// <remarks>
            /// Periodic:SendZeroRewards
            /// </remarks>
            bool SendZeroRewards { get; }

            /// <summary>
            /// Whether players in spectator mode affect reward calcuations.
            /// This only affects whether the player is included in player counts.
            /// Players in spectator mode are never awarded points.
            /// </summary>
            /// <remarks>
            /// Periodic:IncludeSpectators
            /// </remarks>
            bool IncludeSpectators { get; }

            /// <summary>
            /// Whether players in safe zones affect reward calculations.
            /// This only affects whether the player is included in player counts.
            /// Players in a safe zone are never awarded points.
            /// </summary>
            /// <remarks>
            /// Periodic:IncludeSafeZones
            /// </remarks>
            bool IncludeSafeZones { get; }
        }

        /// <summary>
        /// Gets the rewards to be given to teams.
        /// </summary>
        /// <param name="arena">The arena to get reward info for.</param>
        /// <param name="settings">The settings for the arena.</param>
        /// <param name="freqPoints">The dictionary to populate with reward info. Key = freq, Value = # of points to award.</param>
        void GetRewardPoints(
            Arena arena,
            ISettings settings,
            Dictionary<short, short> freqPoints);
    }
}

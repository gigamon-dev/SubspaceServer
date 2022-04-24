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
            /// Periodic:RewardPoints - For determining how many points to award.
            /// </summary>
            int RewardPoints { get; }

            /// <summary>
            /// Periodic:SplitPoints - Whether points should be split among members of a team.
            /// </summary>
            bool SplitPoints { get; }

            /// <summary>
            /// Periodic:SendZeroRewards - Whether rewards with 0 points should be sent.
            /// </summary>
            bool SendZeroRewards { get; }
        }

        /// <summary>
        /// Data about a team which can be used to determine the team's reward.
        /// </summary>
        public interface ITeamData
        {
            /// <summary>
            /// The players on a team, based on periodic reward rules.
            /// </summary>
            public IReadOnlySet<Player> Players { get; }

            /// <summary>
            /// The # of flags the team owns.
            /// </summary>
            public int FlagCount { get; }
        }

        /// <summary>
        /// Gets the rewards to be given to teams.
        /// </summary>
        /// <param name="arena">The arena to get reward info for.</param>
        /// <param name="settings">The settings for the arena.</param>
        /// <param name="totalPlayerCount">The total # of players, based on periodic reward rules.</param>
        /// <param name="teams">A dictionary containing data about the teams. Key = freq, Value = Data about the team.</param>
        /// <param name="freqPoints">The dictionary to populate with reward info. Key = freq, Value = # of points to award.</param>
        void GetRewardPoints(
            Arena arena, 
            ISettings settings, 
            int totalPlayerCount,
            IReadOnlyDictionary<short, ITeamData> teams,
            IDictionary<short, short> freqPoints);
    }
}

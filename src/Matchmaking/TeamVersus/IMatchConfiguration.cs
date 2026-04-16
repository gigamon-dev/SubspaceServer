using OpenSkillSharp;
using SS.Matchmaking.OpenSkill;

namespace SS.Matchmaking.TeamVersus
{
    /// <summary>
    /// Configuration settings for a match.
    /// </summary>
    public interface IMatchConfiguration
    {
        /// <summary>
        /// Id for game type, for when saving to the database. <see langword="null"/> means the stats will not be saved.
        /// </summary>
        long? GameTypeId { get; }

        /// <summary>
        /// The required # of teams to begin a match.
        /// </summary>
        int NumTeams { get; }

        /// <summary>
        /// The required # of players per team to begin a match.
        /// </summary>
        int PlayersPerTeam { get; }

        /// <summary>
        /// The # of lives each player begins with.
        /// </summary>
        int LivesPerPlayer { get; }

        /// <summary>
        /// The duration of the match.
        /// </summary>
        /// <remarks>
        /// <see langword="null"/> means no limit.
        /// </remarks>
        TimeSpan? TimeLimit { get; }

        /// <summary>
        /// The duration of overtime.
        /// </summary>
        /// <remarks>
        /// <see langword="null"/> means no limit.
        /// </remarks>
        TimeSpan? OverTimeLimit { get; }

        /// <summary>
        /// Gets the configuration of the match boxes.
        /// </summary>
        ReadOnlySpan<IMatchBoxConfiguration> Boxes { get; }

        /// <summary>
        /// The OpenSkill model.
        /// </summary>
        IOpenSkillModel OpenSkillModel { get; }

        /// <summary>
        /// The amount of decay to add to an OpenSkill rating's sigma per day of inactivity.
        /// </summary>
        double OpenSkillSigmaDecayPerDay { get; }

        /// <summary>
        /// Whether to rate using scores, rather than ranks, when it's possible.
        /// </summary>
        bool OpenSkillUseScoresWhenPossible { get; }

        /// <summary>
        /// The arguments to pass when calculating the Ordinal value to display for a rating.
        /// </summary>
        public OrdinalArgs OpenSkillDisplayOrdinal { get; }

        /// <summary>
        /// Additional players beyond the minimum (N) to consider for look-ahead balancing. 0 = disabled (FIFO).
        /// </summary>
        int LookAheadWindow => 0;

        /// <summary>
        /// Seconds to wait for the look-ahead window to fill before forming with fewer than N+W candidates.
        /// </summary>
        int LookAheadWaitSeconds => 60;

        /// <summary>
        /// Fraction per skip that nudges an outlier's effective ordinal toward the candidate pool mean.
        /// After skipCount * rate >= 1.0, the player is effectively at the mean.
        /// </summary>
        double SkipNudgeRate => 0.2;

        /// <summary>
        /// Maximum ordinal gap a strict-mode player tolerates between themselves and the lowest-rated
        /// player in the selected set. 0 = no limit.
        /// </summary>
        double StrictMatchmakingMaxDisparity => 0;
    }
}

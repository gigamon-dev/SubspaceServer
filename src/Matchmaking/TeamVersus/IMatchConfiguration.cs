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
    }
}

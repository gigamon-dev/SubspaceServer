namespace SS.Matchmaking.TeamVersus
{
    public interface IMatchConfiguration
    {
        /// <summary>
        /// Id for game type, for when saving to the database.
        /// </summary>
        long GameTypeId { get; }

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
    }
}

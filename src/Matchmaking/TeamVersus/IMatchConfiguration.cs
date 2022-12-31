namespace SS.Matchmaking.TeamVersus
{
    public interface IMatchConfiguration
    {
        int NumTeams { get; }
        int PlayersPerTeam { get; }
    }
}

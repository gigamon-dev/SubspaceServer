using System.Collections.ObjectModel;

namespace SS.Matchmaking.TeamVersus
{
    public interface IMatchData
    {
        MatchIdentifier MatchIdentifier { get; }
        IMatchConfiguration Configuration { get; }
        string ArenaName { get; }
        ReadOnlyCollection<ITeam> Teams { get; }
        DateTime? Started { get; }
    }
}

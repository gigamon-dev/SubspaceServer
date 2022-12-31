using SS.Core;

namespace SS.Matchmaking.TeamVersus
{
    public interface IPlayerSlot
    {
        IMatchData MatchData { get; }

        string PlayerName { get; }

        Player Player { get; }

        int LagOuts { get; }
        int Lives { get; }

        // TODO: maybe items too so that the stats module can track how many wasted items
    }
}

using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback delegate for when a Team Versus match has started.
    /// </summary>
    /// <param name="matchData">The match that started.</param>
    [ComponentCallback]
    public delegate void TeamVersusMatchStartedCallback(IMatchData matchData);
}

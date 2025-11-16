using SS.Core;
using SS.Matchmaking.League;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback delegate for when a league match has ended.
    /// </summary>
    /// <param name="leagueMatch"></param>
    [ComponentCallback]
    public delegate void LeagueMatchEndedCallback(ILeagueMatch leagueMatch);
}

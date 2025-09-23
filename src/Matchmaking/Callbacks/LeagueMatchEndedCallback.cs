using SS.Core;
using SS.Matchmaking.League;

namespace SS.Matchmaking.Callbacks
{
    [CallbackHelper]
    public static partial class LeagueMatchEndedCallback
    {
        public delegate void LeagueMatchEndedDelegate(ILeagueMatch leagueMatch);
    }
}

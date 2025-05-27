using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    [CallbackHelper]
    public static partial class TeamVersusStatsPlayerKilledCallback
    {
        public delegate void TeamVersusStatsPlayerKilledDelegate(IPlayerSlot killedSlot, IMemberStats killedStats, IPlayerSlot killerSlot, IMemberStats killerStats, bool isKnockout);
    }
}

using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    [CallbackHelper]
    public static partial class TeamVersusMatchPlayerKilledCallback
    {
        public delegate void TeamVersusMatchPlayerKilledDelegate(IPlayerSlot killedSlot, IPlayerSlot killerSlot, bool isKnockout);
    }
}

using SS.Core;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback delegate for when a player is killed in a Team Versus match.
    /// </summary>
    /// <remarks>
    /// This callback includes data about stats, as opposed to <see cref="TeamVersusMatchPlayerKilledCallback"/> which only has data about the match.
    /// </remarks>
    /// <param name="killedSlot"></param>
    /// <param name="killedStats"></param>
    /// <param name="killerSlot"></param>
    /// <param name="killerStats"></param>
    /// <param name="isKnockout"></param>
    [ComponentCallback]
    public delegate void TeamVersusStatsPlayerKilledCallback(IPlayerSlot killedSlot, IMemberStats killedStats, IPlayerSlot killerSlot, IMemberStats killerStats, bool isKnockout);
}

using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback delegate for when a 1v1 match has started.
    /// </summary>
    /// <param name="arena"></param>
    /// <param name="boxId"></param>
    /// <param name="player1"></param>
    /// <param name="player2"></param>
    [ComponentCallback]
    public delegate void OneVersusOneMatchStartedCallback(Arena arena, int boxId, Player player1, Player player2);
}

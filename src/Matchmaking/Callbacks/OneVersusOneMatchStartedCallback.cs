using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a 1v1 match starts.
    /// </summary>
    [CallbackHelper]
    public static partial class OneVersusOneMatchStartedCallback
    {
        public delegate void OneVersusOneMatchStartedDelegate(Arena arena, int boxId, Player player1, Player player2);
    }
}

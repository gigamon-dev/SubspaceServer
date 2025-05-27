using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    public enum OneVersusOneMatchEndReason
    {
        /// <summary>
        /// A winner was decided.
        /// </summary>
        Decided,

        /// <summary>
        /// Ended in a draw (Double knockout).
        /// </summary>
        Draw,

        /// <summary>
        /// Ended because one or both players gave up (change to spec, changed arenas, disconnected).
        /// </summary>
        Aborted,
    }

    /// <summary>
    /// Callback for when a 1v1 match ends.
    /// </summary>
    [CallbackHelper]
    public static partial class OneVersusOneMatchEndedCallback
    {
        public delegate void OneVersusOneMatchEndedDelegate(Arena arena, int boxId, OneVersusOneMatchEndReason reason, string? winnerPlayerName);
    }
}

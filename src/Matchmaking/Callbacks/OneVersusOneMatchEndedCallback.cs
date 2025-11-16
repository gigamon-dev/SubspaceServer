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
    /// Callback delegate for when a 1v1 match has ended.
    /// </summary>
    /// <param name="arena"></param>
    /// <param name="boxId"></param>
    /// <param name="reason"></param>
    /// <param name="winnerPlayerName"></param>
    [ComponentCallback]
    public delegate void OneVersusOneMatchEndedCallback(Arena arena, int boxId, OneVersusOneMatchEndReason reason, string? winnerPlayerName);
}

namespace SS.Matchmaking.TeamVersus
{
    public enum MatchEndReason
    {
        /// <summary>
        /// A winner was decided.
        /// </summary>
        Decided,

        /// <summary>
        /// Ended in a draw.
        /// </summary>
        Draw,

        /// <summary>
        /// The match was aborted (?recyclearena or ?shutdown).
        /// </summary>
        Aborted,
    }
}

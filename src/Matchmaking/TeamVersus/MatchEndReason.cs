namespace SS.Matchmaking.TeamVersus
{
    /// <summary>
    /// Represents the reason why a match ended.
    /// </summary>
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
        /// The match was cancelled.
        /// </summary>
        Cancelled,

        /// <summary>
        /// The match is being restarted.
        /// </summary>
        //Restarting, // TODO: add a command for players to request a ?restart, or ?randomize to restart with teams randomized.

        /// <summary>
        /// The module is being unloaded. Usually due to the server shutting down or recycling.
        /// </summary>
        //Shutdown, // TODO: add logic to end ongoing matches upon module unload
    }
}

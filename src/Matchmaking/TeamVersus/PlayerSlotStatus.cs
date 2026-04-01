namespace SS.Matchmaking.TeamVersus
{
    /// <summary>
    /// The state of slot on a team for a player.
    /// </summary>
    public enum PlayerSlotStatus
    {
        /// <summary>
        /// The slot is unused and should not be filled.
        /// </summary>
        None,

        /// <summary>
        /// The slot is waiting to be filled or refilled.
        /// </summary>
        Waiting,

        /// <summary>
        /// The slot is filled.
        /// </summary>
        Playing,

        /// <summary>
        /// The slot was defeated.
        /// </summary>
        KnockedOut,
    }
}

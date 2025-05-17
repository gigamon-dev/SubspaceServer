namespace SS.Matchmaking.TeamVersus
{
    /// <summary>
    /// Reasons why a slot in a Team Versus match has become inactive (considered a "lag out").
    /// </summary>
    public enum SlotInactiveReason
    {
        /// <summary>
        /// The player changed to spectator mode.
        /// </summary>
        ChangedToSpec,

        /// <summary>
        /// The player left the arena.
        /// </summary>
        LeftArena,
    }
}

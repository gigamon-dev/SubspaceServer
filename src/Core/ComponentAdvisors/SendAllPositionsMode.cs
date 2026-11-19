namespace SS.Core.ComponentAdvisors
{
    /// <summary>
    /// Who is sent every position packet of a player in a game (regardless of distance),
    /// for an <see cref="IPlayerPositionAdvisor"/> that provides position packet recipients.
    /// </summary>
    public enum SendAllPositionsMode
    {
        /// <summary>
        /// Nobody. Position packets are sent using the normal rules (based on distance, etc.).
        /// </summary>
        Off,

        /// <summary>
        /// Only the players that a staff member selected (with the ?sendallpos command), whether they're in a ship or spectating.
        /// Unlike the other modes, this does not depend on the game being small.
        /// Everyone else is sent position packets using the normal rules.
        /// </summary>
        Selected,

        /// <summary>
        /// Players in ships that are in the game, and spectators that a staff member selected (with the ?sendallpos command).
        /// Other spectators are sent position packets using the normal rules.
        /// </summary>
        InShip,

        /// <summary>
        /// Players in ships that are in the game, and spectators that are actively watching it
        /// (following a player in the game, and not idle).
        /// Other spectators are sent position packets using the normal rules.
        /// </summary>
        All,
    }
}

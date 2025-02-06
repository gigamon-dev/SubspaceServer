namespace SS.Matchmaking.TeamVersus
{
    /// <summary>
    /// Configuration settings for a match box.
    /// </summary>
    public interface IMatchBoxConfiguration
    {
        /// <summary>
        /// The name of the map region that designates the play area for the match.
        /// <see langword="null"/> means the match does not use play area mechanics.
        /// </summary>
        /// <remarks>
        /// "Play area" is a game mechanic where a team is eliminated when there are no remaining team members in the designated region.
        /// For example, in classic SVS 2v2 matches, players respawn outside of the play area (into a safe zone) and must attach to their teammate to reenter the play area.
        /// </remarks>
        public string? PlayAreaMapRegion { get; }
    }
}

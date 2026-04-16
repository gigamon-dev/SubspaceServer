namespace SS.Matchmaking.TeamVersus
{
    /// <summary>
    /// A player candidate for look-ahead matchmaking selection.
    /// </summary>
    public readonly struct PlayerCandidate
    {
        public required string PlayerName { get; init; }

        /// <summary>
        /// Number of times this player was in the look-ahead window but was not selected.
        /// Used to compute a priority boost when running the selection algorithm.
        /// </summary>
        public int SkipCount { get; init; }
    }
}

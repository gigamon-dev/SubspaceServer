namespace SS.Matchmaking.TeamVersus
{
    /// <summary>
    /// Interface that exposes stats for a member of a team.
    /// </summary>
    public interface IMemberStats
    {
        public short Kills { get; }
        public short Deaths { get; }
    }
}

namespace SS.Matchmaking.TeamVersus
{
    public class TeamLineup
    {
        /// <summary>
        /// Whether the team is from a premade group of players.
        /// </summary>
        public bool IsPremade;

        /// <summary>
        /// The names of the players on the team.
        /// </summary>
        public readonly HashSet<string> Players = new(StringComparer.OrdinalIgnoreCase);
    }
}

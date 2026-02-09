namespace SS.Matchmaking.League
{
    /// <summary>
    /// Interface for a match that is for a league game.
    /// </summary>
    public interface ILeagueMatch : IMatch
    {
        /// <summary>
        /// The league game info.
        /// </summary>
        public LeagueGameInfo LeagueGame { get; }

        /// <summary>
        /// The name of the arena that the match is being held in.
        /// </summary>
        public string ArenaName { get; }
    }
}

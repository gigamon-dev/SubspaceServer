namespace SS.Matchmaking.League
{
    public interface ILeagueMatch : IMatch
    {
        public long SeasonGameId { get; }
        public string ArenaName { get; }
    }
}

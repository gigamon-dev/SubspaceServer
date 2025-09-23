namespace SS.Matchmaking.League
{
    /// <summary>
    /// Status code returned by the league.start_game database function.
    /// </summary>
    public enum GameStartStatus
    {
        /// <summary>
        /// Success, <see cref="LeagueGameInfo"/> is included.
        /// </summary>
        Success = 200,

        /// <summary>
        /// Invalid p_season_game_id.
        /// </summary>
        NotFound = 404,

        /// <summary>
        /// p_season_game_id was valid, but it could not be updated due to being in the wrong state and/or p_force not being true
        /// </summary>
        Conflict = 409,
    }
}

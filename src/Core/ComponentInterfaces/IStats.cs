namespace SS.Core.ComponentInterfaces
{
    //public struct StatCode<T>
    //{
    //    public int StatId { get; private set; }

    //    public StatCode(int statId)
    //    {
    //        StatId = statId;
    //    }
    //}
    
    /// <summary>
    /// Interface for managing statistics on players.
    /// </summary>
    public interface IStats : IComponentInterface
    {
        /// <summary>
        /// Increments a player's statistic in ALL intervals.
        /// </summary>
        /// <param name="p">The player to increment the stat for.</param>
        /// <param name="statId">The statistic to increment.</param>
        /// <param name="amount">The amount to increment by.</param>
        void IncrementStat(Player p, int statId, int amount);

        void StartTimer(Player p, int statId);

        void StopTimer(Player p, int statId);

        /// <summary>
        /// Sets a player's statistic to a specified value.
        /// </summary>
        /// <param name="p">The player to set the stat for.</param>
        /// <param name="statId">The statistic to set.</param>
        /// <param name="interval">The interval to set the stat for.</param>
        /// <param name="value">The value to set.</param>
        void SetStat(Player p, int statId, PersistInterval interval, int value);

        /// <summary>
        /// Gets the value of a player's statistic.
        /// </summary>
        /// <param name="p">The player to get the stat for.</param>
        /// <param name="statId">The statistic to get.</param>
        /// <param name="interval">The interval to get the stat for.</param>
        /// <param name="value">The value, if the stat could be found; otherwise 0.</param>
        /// <returns>Whether the stat could be found.</returns>
        bool TryGetStat(Player p, int statId, PersistInterval interval, out int value);
    }

    /// <summary>
    /// Interface for managing standard Subspace player stats (i.e., points, flag points, kills, deaths).
    /// </summary>
    public interface IScoreStats : IStats, IComponentInterface
    {
        /// <summary>
        /// Looks players with dirty score stats (basic score stats only, i.e., kill points, flag points, kills, deaths).
        /// 
        /// <para>
        /// For players that have dirty stats:
        /// the stat is set as no longer dirty,
        /// the score data is copied to the <see cref="Player.Packet"/>, 
        /// and a <see cref="Packets.Game.S2C_ScoreUpdate"/> packet is sent.
        /// </para>
        /// </summary>
        /// <param name="arena">The arena to send updates for. <see langword="null"/> for all arenas.</param>
        /// <param name="exclude">An optional player to exlude from both the check and any packet send.</param>
        void SendUpdates(Arena arena, Player exclude);

        /// <summary>
        /// Resets all of a player's stats for an interval.
        /// </summary>
        /// <param name="p">The player to reset the stats of.</param>
        /// <param name="interval">The interval to reset stats for.</param>
        void ScoreReset(Player p, PersistInterval interval);
    }
}

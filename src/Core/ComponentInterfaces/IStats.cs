using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Identifies a stat and its type. 
    /// </summary>
    /// <typeparam name="T">
    /// The type of stat.
    /// </typeparam>
    /// <remarks>
    /// The type parameter, <typeparamref name="T"/>, is a bit strange.
    /// The purpose of it is to help protect against mismatching a StatId and its type.
    /// The idea being, you keep a static class containing your StatCodes, similar to the <see cref="StatCodes"/> class.
    /// </remarks>
    public struct StatCode<T> where T: struct
    {
        public int StatId { get; private set; }

        public StatCode(int statId)
        {
            StatId = statId;
        }

        public StatCode(StatId statId)
        {
            StatId = (int)statId;
        }
    }

    /// <summary>
    /// Interface for managing statistics on players that affects global (zone-wide) stats only.
    /// </summary>
    public interface IGlobalPlayerStats : IComponentInterface
    {
        #region IncrementStat methods

        /// <summary>
        /// Increments a player's statistic.
        /// </summary>
        /// <param name="player">The player to increment the stat for.</param>
        /// <param name="statCode">The statistic to increment.</param>
        /// <param name="interval">The interval to increment that stat for. <see langword="null"/> means all intervals.</param>
        /// <param name="amount">The amount to increment by.</param>
        void IncrementStat(Player player, StatCode<int> statCode, PersistInterval? interval, int amount);
        ///<inheritdoc cref="IncrementStat"/>
        void IncrementStat(Player player, StatCode<long> statCode, PersistInterval? interval, long amount);
        ///<inheritdoc cref="IncrementStat"/>
        void IncrementStat(Player player, StatCode<uint> statCode, PersistInterval? interval, uint amount);
        ///<inheritdoc cref="IncrementStat"/>
        void IncrementStat(Player player, StatCode<ulong> statCode, PersistInterval? interval, ulong amount);
        ///<inheritdoc cref="IncrementStat"/>
        void IncrementStat(Player player, StatCode<DateTime> statCode, PersistInterval? interval, TimeSpan amount);
        ///<inheritdoc cref="IncrementStat"/>
        void IncrementStat(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval, TimeSpan amount);

        #endregion

        #region SetStat methods

        /// <summary>
        /// Sets a player's statistic to a specified value.
        /// </summary>
        /// <param name="player">The player to set the stat for.</param>
        /// <param name="statCode">The statistic to set.</param>
        /// <param name="interval">The interval to set the stat for.</param>
        /// <param name="value">The value to set.</param>
        void SetStat(Player player, StatCode<int> statCode, PersistInterval interval, int value);
        ///<inheritdoc cref="SetStat"/>
        void SetStat(Player player, StatCode<long> statCode, PersistInterval interval, long value);
        ///<inheritdoc cref="SetStat"/>
        void SetStat(Player player, StatCode<uint> statCode, PersistInterval interval, uint value);
        ///<inheritdoc cref="SetStat"/>
        void SetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, ulong value);
        ///<inheritdoc cref="SetStat"/>
        void SetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, DateTime value);
        ///<inheritdoc cref="SetStat"/>
        void SetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, TimeSpan value);

        #endregion

        #region TryGetStat methods

        /// <summary>
        /// Gets the value of a player's statistic.
        /// </summary>
        /// <param name="player">The player to get the stat for.</param>
        /// <param name="statCode">The statistic to get.</param>
        /// <param name="interval">The interval to get the stat for.</param>
        /// <param name="value">The value, if the stat could be found; otherwise the default value of the type.</param>
        /// <returns>Whether the stat could be found.</returns>
        bool TryGetStat(Player player, StatCode<int> statCode, PersistInterval interval, out int value);
        ///<inheritdoc cref="TryGetStat"/>
        bool TryGetStat(Player player, StatCode<long> statCode, PersistInterval interval, out long value);
        ///<inheritdoc cref="TryGetStat"/>
        bool TryGetStat(Player player, StatCode<uint> statCode, PersistInterval interval, out uint value);
        ///<inheritdoc cref="TryGetStat"/>
        bool TryGetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, out ulong value);
        ///<inheritdoc cref="TryGetStat"/>
        bool TryGetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, out DateTime value);
        ///<inheritdoc cref="TryGetStat"/>
        bool TryGetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, out TimeSpan value);

        #endregion

        #region Timer (TimeSpan stat) methods

        /// <summary>
        /// Starts the timer for a TimeSpan stat, which acts like a stopwatch.
        /// </summary>
        /// <param name="player">The player to start the timer for.</param>
        /// <param name="statCode">The timer statistic to start.</param>
        /// <param name="interval">The interval to start the timer for. <see langword="null"/> means all intervals.</param>
        void StartTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval);

        /// <summary>
        /// Stops the timer for a TimeSpan stat, which acts like a stopwatch.
        /// </summary>
        /// <param name="player">The player to stop the timer for.</param>
        /// <param name="statCode">The timer statistic to stop.</param>
        /// <param name="interval">The interval to stop the timer for. <see langword="null"/> means all intervals.</param>
        void StopTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval);

        /// <summary>
        /// Reset the timer for a TimeSpan stat, which acts like a stopwatch.
        /// Resetting a timer stops the timer and resets the elapsed duration back to zero.
        /// </summary>
        /// <param name="player">The player to reset the timer for.</param>
        /// <param name="statCode">The timer statistic to reset.</param>
        /// <param name="interval">The interval to reset the timer for. <see langword="null"/> means all intervals.</param>
        void ResetTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval);

        #endregion
    }

    /// <summary>
    /// Interface for managing statistics on players that affects current arena stats only.
    /// </summary>
    /// <remarks>
    /// This is the equivalent of the IStats interface in ASSS since it only tracks stats per-arena.
    /// </remarks>
    public interface IArenaPlayerStats : IComponentInterface
    {
        #region IncrementStat methods

        /// <inheritdoc cref="IGlobalPlayerStats.IncrementStat"/>
        void IncrementStat(Player player, StatCode<int> statCode, PersistInterval? interval, int amount);
        /// <inheritdoc cref="IGlobalPlayerStats.IncrementStat"/>
        void IncrementStat(Player player, StatCode<long> statCode, PersistInterval? interval, long amount);
        /// <inheritdoc cref="IGlobalPlayerStats.IncrementStat"/>
        void IncrementStat(Player player, StatCode<uint> statCode, PersistInterval? interval, uint amount);
        /// <inheritdoc cref="IGlobalPlayerStats.IncrementStat"/>
        void IncrementStat(Player player, StatCode<ulong> statCode, PersistInterval? interval, ulong amount);
        /// <inheritdoc cref="IGlobalPlayerStats.IncrementStat"/>
        void IncrementStat(Player player, StatCode<DateTime> statCode, PersistInterval? interval, TimeSpan amount);
        /// <inheritdoc cref="IGlobalPlayerStats.IncrementStat"/>
        void IncrementStat(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval, TimeSpan amount);

        #endregion

        #region SetStat methods

        /// <inheritdoc cref="IGlobalPlayerStats.SetStat"/>
        void SetStat(Player player, StatCode<int> statCode, PersistInterval interval, int value);
        /// <inheritdoc cref="IGlobalPlayerStats.SetStat"/>
        void SetStat(Player player, StatCode<long> statCode, PersistInterval interval, long value);
        /// <inheritdoc cref="IGlobalPlayerStats.SetStat"/>
        void SetStat(Player player, StatCode<uint> statCode, PersistInterval interval, uint value);
        /// <inheritdoc cref="IGlobalPlayerStats.SetStat"/>
        void SetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, ulong value);
        /// <inheritdoc cref="IGlobalPlayerStats.SetStat"/>
        void SetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, DateTime value);
        /// <inheritdoc cref="IGlobalPlayerStats.SetStat"/>
        void SetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, TimeSpan value);

        #endregion

        #region TryGetStat methods

        /// <inheritdoc cref="IGlobalPlayerStats.TryGetStat"/>
        bool TryGetStat(Player player, StatCode<int> statCode, PersistInterval interval, out int value);
        /// <inheritdoc cref="IGlobalPlayerStats.TryGetStat"/>
        bool TryGetStat(Player player, StatCode<long> statCode, PersistInterval interval, out long value);
        /// <inheritdoc cref="IGlobalPlayerStats.TryGetStat"/>
        bool TryGetStat(Player player, StatCode<uint> statCode, PersistInterval interval, out uint value);
        /// <inheritdoc cref="IGlobalPlayerStats.TryGetStat"/>
        bool TryGetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, out ulong value);
        /// <inheritdoc cref="IGlobalPlayerStats.TryGetStat"/>
        bool TryGetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, out DateTime value);
        /// <inheritdoc cref="IGlobalPlayerStats.TryGetStat"/>
        bool TryGetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, out TimeSpan value);

        #endregion

        #region Timer (TimeSpan stat) methods

        /// <inheritdoc cref="IGlobalPlayerStats.StartTimer"/>
        void StartTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval);
        /// <inheritdoc cref="IGlobalPlayerStats.StopTimer"/>
        void StopTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval);
        /// <inheritdoc cref="IGlobalPlayerStats.ResetTimer"/>
        void ResetTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval);

        #endregion
    }

    /// <summary>
    /// Interface for managing statistics on players that affects both global (zone-wide) and current arena stats.
    /// </summary>
    public interface IAllPlayerStats : IComponentInterface
    {
        #region IncrementStat methods

        /// <inheritdoc cref="IGlobalPlayerStats.IncrementStat"/>
        void IncrementStat(Player player, StatCode<int> statCode, PersistInterval? interval, int amount);
        /// <inheritdoc cref="IGlobalPlayerStats.IncrementStat"/>
        void IncrementStat(Player player, StatCode<long> statCode, PersistInterval? interval, long amount);
        /// <inheritdoc cref="IGlobalPlayerStats.IncrementStat"/>
        void IncrementStat(Player player, StatCode<uint> statCode, PersistInterval? interval, uint amount);
        /// <inheritdoc cref="IGlobalPlayerStats.IncrementStat"/>
        void IncrementStat(Player player, StatCode<ulong> statCode, PersistInterval? interval, ulong amount);
        /// <inheritdoc cref="IGlobalPlayerStats.IncrementStat"/>
        void IncrementStat(Player player, StatCode<DateTime> statCode, PersistInterval? interval, TimeSpan amount);
        /// <inheritdoc cref="IGlobalPlayerStats.IncrementStat"/>
        void IncrementStat(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval, TimeSpan amount);

        #endregion

        #region SetStat methods

        /// <inheritdoc cref="IGlobalPlayerStats.SetStat"/>
        void SetStat(Player player, StatCode<int> statCode, PersistInterval interval, int value);
        /// <inheritdoc cref="IGlobalPlayerStats.SetStat"/>
        void SetStat(Player player, StatCode<long> statCode, PersistInterval interval, long value);
        /// <inheritdoc cref="IGlobalPlayerStats.SetStat"/>
        void SetStat(Player player, StatCode<uint> statCode, PersistInterval interval, uint value);
        /// <inheritdoc cref="IGlobalPlayerStats.SetStat"/>
        void SetStat(Player player, StatCode<ulong> statCode, PersistInterval interval, ulong value);
        /// <inheritdoc cref="IGlobalPlayerStats.SetStat"/>
        void SetStat(Player player, StatCode<DateTime> statCode, PersistInterval interval, DateTime value);
        /// <inheritdoc cref="IGlobalPlayerStats.SetStat"/>
        void SetStat(Player player, StatCode<TimeSpan> statCode, PersistInterval interval, TimeSpan value);

        #endregion

        // No Get methods on purpose. Wouldn't know which stat to get, the global one or the current arena's.

        #region Timer (TimeSpan stat) methods

        /// <inheritdoc cref="IGlobalPlayerStats.StartTimer"/>
        void StartTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval);
        /// <inheritdoc cref="IGlobalPlayerStats.StopTimer"/>
        void StopTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval);
        /// <inheritdoc cref="IGlobalPlayerStats.ResetTimer"/>
        void ResetTimer(Player player, StatCode<TimeSpan> statCode, PersistInterval? interval);

        #endregion
    }

    /// <summary>
    /// Interface for managing standard Subspace player stats (i.e., points, flag points, kills, deaths).
    /// </summary>
    public interface IScoreStats : IComponentInterface
    {
        /// <summary>
        /// Gets scores for a player based on the player's current arena.
        /// </summary>
        /// <param name="player">The player to get score stats for.</param>
        /// <param name="killPoints">The # of points the player has from kills.</param>
        /// <param name="flagPoints">The # of points the player has from flags.</param>
        /// <param name="kills">The # of kills the player has.</param>
        /// <param name="deaths">The # of deaths the player has.</param>
        void GetScores(Player player, out int killPoints, out int flagPoints, out short kills, out short deaths);

        /// <summary>
        /// Looks players with dirty score stats (basic score stats only, i.e., kill points, flag points, kills, deaths).
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
        /// <param name="player">The player to reset the stats of.</param>
        /// <param name="interval">The interval to reset stats for.</param>
        void ScoreReset(Player player, PersistInterval interval);
    }
}

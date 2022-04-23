using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SS.Core
{
    public static class StatCodes
    {
        /* Standard Subspace scores
         * 
         * KillPoints and FlagPoints
         * - The Subspace protocol actually represents this as an Int32.
         * - However, this server stores it as an UInt64. This makes stats for the 'forever' interval (which never are reset) less likely to overflow.
         * 
         * Kills and Deaths
         * - The Subspace protocol actually represents this as an Int16.
         * - However, this server stores it as an UInt64.
         */
        public static readonly StatCode<ulong> KillPoints = new(StatId.KillPoints); 
        public static readonly StatCode<ulong> FlagPoints = new(StatId.FlagPoints);
        public static readonly StatCode<ulong> Kills = new(StatId.Kills);
        public static readonly StatCode<ulong> Deaths = new(StatId.Deaths);

        public static readonly StatCode<ulong> TeamKills = new(StatId.TeamKills);
        public static readonly StatCode<ulong> TeamDeaths = new(StatId.TeamDeaths);
        public static readonly StatCode<TimeSpan> ArenaTotalTime = new(StatId.ArenaTotalTime);

        public static readonly StatCode<ulong> FlagPickups = new(StatId.FlagPickups);
        public static readonly StatCode<TimeSpan> FlagCarryTime = new(StatId.FlagCarryTime);
        public static readonly StatCode<ulong> FlagDrops = new(StatId.FlagDrops);
        public static readonly StatCode<ulong> FlagNeutDrops = new(StatId.FlagNeutDrops);
        public static readonly StatCode<ulong> FlagKills = new(StatId.FlagKills);
        public static readonly StatCode<ulong> FlagDeaths = new(StatId.FlagDeaths);
        public static readonly StatCode<ulong> FlagGamesWon = new(StatId.FlagGamesWon);
        public static readonly StatCode<ulong> FlagGamesLost = new(StatId.FlagGamesLost);
        public static readonly StatCode<ulong> TurfTags = new(StatId.TurfTags);

        public static readonly StatCode<ulong> BallCarries = new(StatId.BallCarries);
        public static readonly StatCode<TimeSpan> BallCarryTime = new(StatId.BallCarryTime);
        public static readonly StatCode<ulong> BallGoals = new(StatId.BallGoals);
        public static readonly StatCode<ulong> BallGamesWon = new(StatId.BallGamesWon);
        public static readonly StatCode<ulong> BallGamesLost = new(StatId.BallGamesLost);

        public static readonly StatCode<ulong> KothGamesWon = new(StatId.KothGamesWon);
        public static readonly StatCode<ulong> SpeedGamesWon = new(StatId.SpeedGamesWon);
        public static readonly StatCode<uint> SpeedPersonalBest = new(StatId.SpeedPersonalBest);
    }

    public enum StatId
    {
        //
        // These four correspond to standard subspace statistics
        //

        KillPoints = 0,
        FlagPoints,
        Kills,
        Deaths,

        //
        // General purpose
        //

        Assists = 100,
        TeamKills,
        TeamDeaths,
        ArenaTotalTime,
        ArenaSpecTime,
        DamageTaken,
        DamageDealt,

        //
        // Flag games
        //

        FlagPickups = 200,
        FlagCarryTime,
        FlagDrops,
        FlagNeutDrops,
        FlagKills,
        FlagDeaths,
        FlagGamesWon,
        FlagGamesLost,
        TurfTags,

        //
        // Ball games
        //

        /// <summary>
        /// # of times a player has carried the ball
        /// </summary>
        BallCarries = 300,

        /// <summary>
        /// Amount of time a player has carried the ball
        /// </summary>
        BallCarryTime,

        /// <summary>
        /// # of times a player has scored a goal
        /// </summary>
        BallGoals,

        /// <summary>
        /// # of ball games won
        /// </summary>
        BallGamesWon,

        /// <summary>
        /// # of ball games lost
        /// </summary>
        BallGamesLost,

        // Other games
        KothGamesWon = 400,
        SpeedGamesWon = 410,
        SpeedPersonalBest, // PersistInterval.Forever only
        //JackpotGamesWon = 420,
        //RabbitGamesWon = 430,

        //
        // Extended ball stats
        //

        /// <summary>
        /// # of goal assists
        /// </summary>
        BallAssists = 500,

        /// <summary>
        /// # of times a player has stolen the ball (within a certain amount of time)
        /// </summary>
        BallSteals,

        /// <summary>
        /// # of times a player has stolen the ball (greater than a certain amount of time)
        /// </summary>
        BallDelayedSteals,

        /// <summary>
        /// # of times a player has turned the ball over (within a certain amount of time)
        /// </summary>
        BallTurnovers,

        /// <summary>
        /// # of times a player has turned the ball over (greater than a certain amount of time)
        /// </summary>
        BallDelayedTurnovers,

        /// <summary>
        /// # of times a player has prevented an enemy goal (steals within own goal)
        /// </summary>
        BallSaves,

        /// <summary>
        /// # of times a player has turned the ball over in (turnovers within enemy goal)
        /// </summary>
        BallChokes,

        /// <summary>
        /// # of times a player has killed an enemy ball carrier
        /// </summary>
        BallKills,

        /// <summary>
        /// # of times a player has killed a teammate ball carrier
        /// </summary>
        BallTeamKills,

        /// <summary>
        /// # of times a player has picked up a newly spawned ball
        /// </summary>
        BallSpawns,
    }
}

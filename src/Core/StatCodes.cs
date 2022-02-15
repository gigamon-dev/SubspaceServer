using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SS.Core
{
    public enum StatCode
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
        //JackpotGamesWon,
        //SpeedGamesWon,
        //RabbitGamesWon,

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

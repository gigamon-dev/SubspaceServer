using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using System;

namespace SS.Core.Modules.Scoring
{
    /// <summary>
    /// Module that tracks the following basic player stats:
    /// <list type="bullet">
    ///     <item>
    ///         <term><see cref="StatCodes.ArenaTotalTime"/></term>
    ///         <description>The time duration that a player spent in an arena</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.Kills"/></term>
    ///         <description>Number of kills</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.Deaths"/></term>
    ///         <description>Number of deaths</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.TeamKills"/></term>
    ///         <description>Number teammates killed</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.TeamDeaths"/></term>
    ///         <description>Number deaths by teammate</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.FlagKills"/></term>
    ///         <description>Number of kills while carrying a flag</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.FlagDeaths"/></term>
    ///         <description>Number of deaths while carrying a flag</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.FlagGamesWon"/></term>
    ///         <description>Number of flag games won</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.FlagGamesLost"/></term>
    ///         <description>Number of flag games lost</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.FlagPoints"/></term>
    ///         <description>Number points rewarded for winning flag games</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.FlagPickups"/></term>
    ///         <description>Number of carryable flags picked up</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.FlagCarryTime"/></term>
    ///         <description>Duration that flags have been carried</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.FlagDrops"/></term>
    ///         <description>Number of flags dropped</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.FlagNeutDrops"/></term>
    ///         <description>Number of neutral flags dropped</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.TurfTags"/></term>
    ///         <description>Number of static flags tagged</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.BallCarryTime"/></term>
    ///         <description>Duration that of carrying a ball</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.BallCarries"/></term>
    ///         <description>Number of ball carries</description>
    ///     </item>
    ///     <item>
    ///         <term><see cref="StatCodes.BallGoals"/></term>
    ///         <description>Number of ball goals</description>
    ///     </item>
    /// </list>
    /// </summary>
    [CoreModuleInfo]
    public class BasicStats : IModule
    {
        private IPlayerData _playerData;
        private IAllPlayerStats _allPlayerStats;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IPlayerData playerData,
            IAllPlayerStats allPlayerStats)
        {
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _allPlayerStats = allPlayerStats ?? throw new ArgumentNullException(nameof(allPlayerStats));

            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            KillCallback.Register(broker, Callback_Kill);

            FlagGameResetCallback.Register(broker, Callback_FlagGameReset);
            FlagGainCallback.Register(broker, Callback_FlagGain);
            FlagLostCallback.Register(broker, Callback_FlagLost);
            StaticFlagClaimedCallback.Register(broker, Callback_StaticFlagClaimed);

            BallPickupCallback.Register(broker, Callback_BallPickup);
            BallShootCallback.Register(broker, Callback_BallShoot);
            BallGoalCallback.Register(broker, Callback_BallGoal);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            KillCallback.Unregister(broker, Callback_Kill);

            FlagGameResetCallback.Unregister(broker, Callback_FlagGameReset);
            FlagGainCallback.Unregister(broker, Callback_FlagGain);
            FlagLostCallback.Unregister(broker, Callback_FlagLost);
            StaticFlagClaimedCallback.Unregister(broker, Callback_StaticFlagClaimed);

            BallPickupCallback.Unregister(broker, Callback_BallPickup);
            BallShootCallback.Unregister(broker, Callback_BallShoot);
            BallGoalCallback.Unregister(broker, Callback_BallGoal);

            return true;
        }

        #endregion

        private void Callback_PlayerAction(Player player, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterGame)
                _allPlayerStats.StartTimer(player, StatCodes.ArenaTotalTime, null);
            else if (action == PlayerAction.LeaveArena)
                _allPlayerStats.StopTimer(player, StatCodes.ArenaTotalTime, null);
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bounty, short flagCount, short points, Prize green)
        {
            _allPlayerStats.IncrementStat(killer, StatCodes.Kills, null, 1);
            _allPlayerStats.IncrementStat(killed, StatCodes.Deaths, null, 1);

            if (killer.Freq == killed.Freq)
            {
                _allPlayerStats.IncrementStat(killer, StatCodes.TeamKills, null, 1);
                _allPlayerStats.IncrementStat(killed, StatCodes.TeamDeaths, null, 1);
            }

            if (flagCount > 0)
            {
                _allPlayerStats.IncrementStat(killer, StatCodes.FlagKills, null, 1);
                _allPlayerStats.IncrementStat(killed, StatCodes.FlagDeaths, null, 1);
            }
        }

        private void Callback_FlagGameReset(Arena arena, short winnerFreq, int points)
        {
            if (winnerFreq > 0 && points > 0)
            {
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Arena == arena
                            && player.Status == PlayerState.Playing
                            && player.Ship != ShipType.Spec)
                        {
                            if (player.Freq == winnerFreq)
                            {
                                _allPlayerStats.IncrementStat(player, StatCodes.FlagGamesWon, null, 1);

                                // only reward points if not in a safe zone
                                if ((player.Position.Status & PlayerPositionStatus.Safezone) != PlayerPositionStatus.Safezone)
                                {
                                    _allPlayerStats.IncrementStat(player, StatCodes.FlagPoints, null, points);
                                }
                            }
                            else
                            {
                                _allPlayerStats.IncrementStat(player, StatCodes.FlagGamesLost, null, 1);
                            }
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
        }

        private void Callback_FlagGain(Arena arena, Player player, short flagId, FlagPickupReason reason)
        {
            if (reason == FlagPickupReason.Pickup)
            {
                _allPlayerStats.IncrementStat(player, StatCodes.FlagPickups, null, 1);
            }

            _allPlayerStats.StartTimer(player, StatCodes.FlagCarryTime, null);
        }

        private void Callback_FlagLost(Arena arena, Player player, short flagId, FlagLostReason reason)
        {
            // Only stop the timer if it was the last flag the player was carrying.
            if (player.Packet.FlagsCarried == 0)
                _allPlayerStats.StopTimer(player, StatCodes.FlagCarryTime, null);

            switch (reason)
            {
                case FlagLostReason.Dropped:
                case FlagLostReason.InSafe:
                    _allPlayerStats.IncrementStat(player, StatCodes.FlagDrops, null, 1);
                    break;
                case FlagLostReason.ShipChange:
                case FlagLostReason.FreqChange:
                case FlagLostReason.LeftArena:
                    _allPlayerStats.IncrementStat(player, StatCodes.FlagNeutDrops, null, 1);
                    break;

                case FlagLostReason.Killed:
                default:
                    break;
            }
        }

        private void Callback_StaticFlagClaimed(Arena arena, Player player, short flagId, short oldFreq, short newFreq)
        {
            _allPlayerStats.IncrementStat(player, StatCodes.TurfTags, null, 1);
        }

        private void Callback_BallPickup(Arena arena, Player player, byte ballId)
        {
            _allPlayerStats.StartTimer(player, StatCodes.BallCarryTime, null);
            _allPlayerStats.IncrementStat(player, StatCodes.BallCarries, null, 1);
        }

        private void Callback_BallShoot(Arena arena, Player player, byte ballId)
        {
            _allPlayerStats.StopTimer(player, StatCodes.BallCarryTime, null);
        }

        private void Callback_BallGoal(Arena arena, Player player, byte ballId, MapCoordinate coordinate)
        {
            _allPlayerStats.IncrementStat(player, StatCodes.BallGoals, null, 1);
        }
    }
}

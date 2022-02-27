using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets.Game;
using System;

namespace SS.Core.Modules.Scoring
{
    public class BasicStats : IModule
    {
        //private IPlayerData _playerData;
        private IAllPlayerStats _allPlayerStats;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            //IPlayerData playerData,
            IAllPlayerStats allPlayerStats)
        {
            //_playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _allPlayerStats = allPlayerStats ?? throw new ArgumentNullException(nameof(allPlayerStats));

            PlayerActionCallback.Register(broker, Callback_PlayerAction);
            KillCallback.Register(broker, Callback_Kill);
            // TODO: flag callbacks
            BallPickupCallback.Register(broker, Callback_BallPickup);
            BallShootCallback.Register(broker, Callback_BallShoot);
            BallGoalCallback.Register(broker, Callback_BallGoal);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            PlayerActionCallback.Unregister(broker, Callback_PlayerAction);
            KillCallback.Register(broker, Callback_Kill);
            // TODO: flag callbacks
            BallPickupCallback.Unregister(broker, Callback_BallPickup);
            BallShootCallback.Unregister(broker, Callback_BallShoot);
            BallGoalCallback.Unregister(broker, Callback_BallGoal);

            return true;
        }

        #endregion

        private void Callback_PlayerAction(Player p, PlayerAction action, Arena arena)
        {
            if (action == PlayerAction.EnterGame)
                _allPlayerStats.StartTimer(p, StatCodes.ArenaTotalTime, null);
            else if (action == PlayerAction.LeaveArena)
                _allPlayerStats.StopTimer(p, StatCodes.ArenaTotalTime, null);
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bty, short flagCount, short pts, Prize green)
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

        private void Callback_BallPickup(Arena arena, Player p, byte ballId)
        {
            _allPlayerStats.StartTimer(p, StatCodes.BallCarryTime, null);
            _allPlayerStats.IncrementStat(p, StatCodes.BallCarries, null, 1);
        }

        private void Callback_BallShoot(Arena arena, Player p, byte ballId)
        {
            _allPlayerStats.StopTimer(p, StatCodes.BallCarryTime, null);
        }

        private void Callback_BallGoal(Arena arena, Player p, byte ballId, MapCoordinate coordinate)
        {
            _allPlayerStats.IncrementStat(p, StatCodes.BallGoals, null, 1);
        }
    }
}

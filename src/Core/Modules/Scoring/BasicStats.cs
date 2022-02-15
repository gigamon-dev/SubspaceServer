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
        private IStats _stats;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            //IPlayerData playerData,
            IStats stats)
        {
            //_playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _stats = stats ?? throw new ArgumentNullException(nameof(stats));

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
                _stats.StartTimer(p, (int)StatCode.ArenaTotalTime);
            else if (action == PlayerAction.LeaveArena)
                _stats.StopTimer(p, (int)StatCode.ArenaTotalTime);
        }

        private void Callback_Kill(Arena arena, Player killer, Player killed, short bty, short flagCount, short pts, Prize green)
        {
            _stats.IncrementStat(killer, (int)StatCode.Kills, 1);
            _stats.IncrementStat(killed, (int)StatCode.Deaths, 1);

            if (killer.Freq == killed.Freq)
            {
                _stats.IncrementStat(killer, (int)StatCode.TeamKills, 1);
                _stats.IncrementStat(killed, (int)StatCode.TeamDeaths, 1);
            }

            if (flagCount > 0)
            {
                _stats.IncrementStat(killer, (int)StatCode.FlagKills, 1);
                _stats.IncrementStat(killed, (int)StatCode.FlagDeaths, 1);
            }
        }

        private void Callback_BallPickup(Arena arena, Player p, byte ballId)
        {
            _stats.StartTimer(p, (int)StatCode.BallCarryTime);
            _stats.IncrementStat(p, (int)StatCode.BallCarries, 1);
        }

        private void Callback_BallShoot(Arena arena, Player p, byte ballId)
        {
            _stats.StopTimer(p, (int)StatCode.BallCarryTime);
        }

        private void Callback_BallGoal(Arena arena, Player p, byte ballId, MapCoordinate coordinate)
        {
            _stats.IncrementStat(p, (int)StatCode.BallGoals, 1);
        }
    }
}

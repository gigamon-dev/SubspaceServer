using SS.Core.ComponentInterfaces;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a player lags out (leaves the arena or changes to spectator mode while in a match) in a Team Versus match.
    /// </summary>
    public static class TeamVersusMatchPlayerLagOutCallback
    {
        /// <summary>
        /// Delegate for when a player lags out in a Team Versus match.
        /// </summary>
        /// <param name="playerSlot">The slot for the player that lagged out.</param>
        /// <param name="reason">The reason for the lag out.</param>
        public delegate void TeamVersusMatchPlayerLagOutDelegate(IPlayerSlot playerSlot, SlotInactiveReason reason);

        public static void Register(IComponentBroker broker, TeamVersusMatchPlayerLagOutDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, TeamVersusMatchPlayerLagOutDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, IPlayerSlot playerSlot, SlotInactiveReason reason)
        {
            broker?.GetCallback<TeamVersusMatchPlayerLagOutDelegate>()?.Invoke(playerSlot, reason);

            if (broker?.Parent != null)
                Fire(broker.Parent, playerSlot, reason);
        }
    }
}

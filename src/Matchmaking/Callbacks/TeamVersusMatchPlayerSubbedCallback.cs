using SS.Core.ComponentInterfaces;
using SS.Matchmaking.TeamVersus;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a player is subbed in for a team versus match.
    /// </summary>
    public static class TeamVersusMatchPlayerSubbedCallback
    {
        /// <summary>
        /// Delegate for when a player is subbed in for a team versus match.
        /// </summary>
        /// <param name="playerSlot">The slot the player was subbed in to.</param>
        /// <param name="subOutPlayerName">The name of the player that was subbed out. <see langword="null"/> for a prevously unassigned slot.</param>
        public delegate void TeamVersusPlayerSlotSubbedDelegate(IPlayerSlot playerSlot, string? subOutPlayerName);

        public static void Register(IComponentBroker broker, TeamVersusPlayerSlotSubbedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, TeamVersusPlayerSlotSubbedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, IPlayerSlot playerSlot, string? subOutPlayerName)
        {
            broker?.GetCallback<TeamVersusPlayerSlotSubbedDelegate>()?.Invoke(playerSlot, subOutPlayerName);

            if (broker?.Parent != null)
                Fire(broker.Parent, playerSlot, subOutPlayerName);
        }
    }
}

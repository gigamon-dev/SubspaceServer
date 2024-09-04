using SS.Core;

namespace SS.Matchmaking.TeamVersus
{
    public interface IPlayerSlot
    {
        /// <summary>
        /// The match the slot is for.
        /// </summary>
        IMatchData MatchData { get; }

        /// <summary>
        /// The team the slot is for.
        /// </summary>
        ITeam Team { get; }

        /// <summary>
        /// The index of the slot in the team (see <see cref="ITeam.Slots"/>).
        /// </summary>
        int SlotIdx { get; }

        /// <summary>
        /// The name of the player that currently fills the slot.
        /// </summary>
        string? PlayerName { get; }

        /// <summary>
        /// The player that currently fills the slot.
        /// </summary>
        /// <remarks>
        /// <see langword="null"/> if the player disconnected.
        /// </remarks>
        Player? Player { get; }

        /// <summary>
        /// Identifies whether the initial player assigned to the slot was in a premade group, and if so, which teammates were in the same group.
        /// </summary>
        /// <remarks>
        /// This may be useful for stats. Such that when stats get saved to a database, it can track a player's group play stats separately from their solo play stats.
        /// </remarks>
        int? PremadeGroupId { get; }

        /// <summary>
        /// The number of times the player has left play (switched to spec or left the arena).
        /// </summary>
        int LagOuts { get; }

        /// <summary>
        /// The number of lives remaining. This includes the currently life.
        /// </summary>
        int Lives { get; }

        #region Ship/Item counts

        /// <summary>
        /// The ship in use.
        /// </summary>
        /// <remarks>
        /// Used to restore the ship when a player returns to a match or subs in.
        /// </remarks>
        public ShipType Ship { get; }

        /// <summary>
        /// The last known item count for bursts.
        /// </summary>
        /// <remarks>
        /// Used to restore the ship when a player returns to a match or subs in.
        /// It can be used to tally wasted items too.
        /// </remarks>
        public byte Bursts { get; }

        /// <summary>
        /// The last known item count for repels.
        /// </summary>
        /// <remarks>
        /// Used to restore the ship when a player returns to a match or subs in.
        /// It can be used to tally wasted items too.
        /// </remarks>
        public byte Repels { get; }

        /// <summary>
        /// The last known item count for thors.
        /// </summary>
        /// <remarks>
        /// Used to restore the ship when a player returns to a match or subs in.
        /// It can be used to tally wasted items too.
        /// </remarks>
        public byte Thors { get; }

        /// <summary>
        /// The last known item count for bricks.
        /// </summary>
        /// <remarks>
        /// Used to restore the ship when a player returns to a match or subs in.
        /// It can be used to tally wasted items too.
        /// </remarks>
        public byte Bricks { get; }

        /// <summary>
        /// The last known item count for decoys.
        /// </summary>
        /// <remarks>
        /// Used to restore the ship when a player returns to a match or subs in.
        /// It can be used to tally wasted items too.
        /// </remarks>
        public byte Decoys { get; }

        /// <summary>
        /// The last known item count for rockets.
        /// </summary>
        /// <remarks>
        /// Used to restore the ship when a player returns to a match or subs in.
        /// It can be used to tally wasted items too.
        /// </remarks>
        public byte Rockets { get; }

        /// <summary>
        /// The last known item count for portals.
        /// </summary>
        /// <remarks>
        /// Used to restore the ship when a player returns to a match or subs in.
        /// It can be used to tally wasted items too.
        /// </remarks>
        public byte Portals { get; }

        #endregion
    }
}

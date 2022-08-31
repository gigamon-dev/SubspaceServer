using SS.Core;
using System.Collections.ObjectModel;

namespace SS.Matchmaking
{
    /// <summary>
    /// Interface for a player group.
    /// </summary>
    public interface IPlayerGroup
    {
        /// <summary>
        /// The group leader.
        /// </summary>
        Player Leader { get; }

        /// <summary>
        /// Players that are members of the group.
        /// </summary>
        ReadOnlyCollection<Player> Members { get; }

        /// <summary>
        /// Players that have been invited to join the group, but haven't yet accepted or declined.
        /// </summary>
        IReadOnlySet<Player> PendingMembers { get; }
    }
}

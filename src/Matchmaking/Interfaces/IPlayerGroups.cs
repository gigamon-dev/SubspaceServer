using SS.Core;

namespace SS.Matchmaking.Interfaces
{
    /// <summary>
    /// Interface for a service that manages player groups.
    /// </summary>
    public interface IPlayerGroups : IComponentInterface
    {
        /// <summary>
        /// Gets a player's group.
        /// </summary>
        /// <param name="player">The player to get the group for.</param>
        /// <returns>The group if the player is in one. Otherwise, null.</returns>
        IPlayerGroup? GetGroup(Player player);
    }
}

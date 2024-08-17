using System.Text;

namespace SS.Core.ComponentAdvisors
{
    /// <summary>
    /// Provides a mechanism to control authorization for an arena.
    /// </summary>
    public interface IArenaAuthorizationAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Checks if a player is authorized to enter a specific arena.
        /// </summary>
        /// <param name="player">The player to check for authorization.</param>
        /// <param name="arena">The arena to check access to.</param>
        /// <param name="errorMessage">An optional message to populate with the reason when not authorized.</param>
        /// <returns><see langword="true"/> if the player is authorized; otherwise, <see langword="false"/>.</returns>
        bool IsAuthorizedToEnter(Player player, Arena arena, StringBuilder? errorMessage) => false;
    }
}

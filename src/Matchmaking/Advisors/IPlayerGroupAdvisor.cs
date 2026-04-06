using SS.Core;
using System.Text;

namespace SS.Matchmaking.Advisors
{
    public interface IPlayerGroupAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Gets whether a <paramref name="player"/> can create a new group.
        /// </summary>
        /// <param name="player">The player to check.</param>
        /// <param name="message">An optional message that the advisor can fill in when it returns <see langword="false"/>.</param>
        /// <returns></returns>
        bool CanPlayerCreateGroup(Player player, StringBuilder message) => true;

        /// <summary>
        /// Gets whether to allow a <paramref name="group"> to send an invite.
        /// </summary>
        /// <remarks>
        /// If any advisor returns <see langword="false"/>, sending an invite will not allowed.
        /// </remarks>
        /// <param name="group">The group to check for.</param>
        /// <param name="message">An optional message that the advisor can fill in when it returns <see langword="false"/>.</param>
        /// <returns><see langword="true"/> if sending an invite should be allowed. Otherwise, <see langword="false"/>.</returns>
        bool CanGroupSendInvite(IPlayerGroup group, StringBuilder message) => true;

        /// <summary>
        /// Gets whether a <paramref name="player"/> is allowed to be be invited to a group.
        /// </summary>
        /// <remarks>
        /// If any advisor returns <see langword="false"/>, sending an invite is not allowed.
        /// </remarks>
        /// <param name="player">The player to check.</param>
        /// <param name="message">An optional message that the advisor can fill in when it returns <see langword="false"/>.</param>
        /// <returns><see langword="true"/> if sending an invite to the <paramref name="player"/> should be allowed. Otherwise, <see langword="false"/>.</returns>
        bool CanPlayerBeInvited(Player player, StringBuilder message) => true;

        /// <summary>
        /// Gets whether a player is allowed to accept a group invite.
        /// </summary>
        /// <remarks>
        /// If any advisor returns <see langword="false"/>, accepting an invite will not be allowed.
        /// </remarks>
        /// <param name="player">The player to check.</param>
        /// <param name="message">An optional message that the advisor can fill in when it returns <see langword="false"/>.</param>
        /// <returns><see langword="true"/> if sending an invite should be allowed. Otherwise, <see langword="false"/>.</returns>
        bool CanPlayerAcceptInvite(Player player, StringBuilder message) => true;
    }
}

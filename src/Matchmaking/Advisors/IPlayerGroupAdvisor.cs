using SS.Core;
using System.Text;

namespace SS.Matchmaking.Advisors
{
    public interface IPlayerGroupAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Gets whether to allow an group to send an invite.
        /// </summary>
        /// <remarks>
        /// If any advisor returns false, sending an invite will not allowed.
        /// </remarks>
        /// <param name="group">The group to check for.</param>
        /// <param name="message">An optional message that the advisor can fill in when it returns false.</param>
        /// <returns>True if sending an invite should be allowed. Otherwise, false.</returns>
        bool AllowSendInvite(IPlayerGroup group, StringBuilder message) => true;

        /// <summary>
        /// Gets whether a player is allowed to accept a group invite.
        /// </summary>
        /// <remarks>
        /// If any advisor returns false, accepting an invite will not be allowed.
        /// </remarks>
        /// <param name="player">The player to check.</param>
        /// <param name="message">An optional message that the advisor can fill in when it returns false.</param>
        /// <returns>True if sending an invite should be allowed. Otherwise, false.</returns>
        bool AllowAcceptInvite(Player player, StringBuilder message) => true;
    }
}

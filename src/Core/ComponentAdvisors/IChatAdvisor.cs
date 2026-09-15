using SS.Core.ComponentInterfaces;
using System;
using System.Text;

namespace SS.Core.ComponentAdvisors
{
    /// <summary>
    /// Interface for an advisor on chat related activities.
    /// </summary>
    public interface IChatAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Called when a player runs a command.
        /// </summary>
        /// <remarks>
        /// If there are multiple advisors, the first to rewrite the command (return true) is used.
        /// </remarks>
        /// <param name="commandChar">The initial character for the command. ? or *</param>
        /// <param name="line">The command line.</param>
        /// <param name="buffer">A buffer to fill with the rewritten command line.</param>
        /// <param name="charsWritten">The number of characters written to <paramref name="buffer"/>.</param>
        /// <returns><see langword="true"/> if the command was rewritten. Otherwise, <see langword="false"/>.</returns>
        bool TryRewriteCommand(char commandChar, ReadOnlySpan<char> line, Span<char> buffer, out int charsWritten)
        {
            charsWritten = 0;
            return false;
        }

        /// <summary>
        /// Checks whether a player is allowed to send a message.
        /// </summary>
        /// <remarks>
        /// This is asked for the message types a player originates: public, team, and private.
        /// Commands are not gated by it, so an advisor that refuses a player's chat still leaves
        /// their commands working.
        /// <para>
        /// Append to <paramref name="errorMessage"/> to tell the player why their message was
        /// dropped. The caller owns it, so do not clear it. A refusal without a message drops the
        /// message silently, which looks to the player like a broken client; prefer to say
        /// something.
        /// </para>
        /// <para>
        /// If there are multiple advisors, the first to refuse decides.
        /// </para>
        /// </remarks>
        /// <param name="player">The player to check for.</param>
        /// <param name="messageType">The type of message being sent.</param>
        /// <param name="errorMessage">An optional message.</param>
        /// <returns><see langword="true"/> to signal that the player is allowed to send the message. Otherwise, <see langword="false"/>.</returns>
        bool CanSendMessage(Player player, ChatMessageType messageType, StringBuilder? errorMessage) => true;
    }
}

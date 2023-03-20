using System;

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
    }
}

using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that provides functionality to filter obscene words from lines of text.
    /// </summary>
    public interface IObscene : IComponentInterface
    {
        /// <summary>
        /// Filters a line of text for obscene words, replacing them with garbage characters.
        /// </summary>
        /// <param name="line">The line to filter.</param>
        /// <returns>True if replacements were made. Otherwise, false.</returns>
        bool Filter(Span<char> line);
    }
}

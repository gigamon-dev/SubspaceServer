using System;

namespace SS.Core.ComponentAdvisors
{
    /// <summary>
    /// Interface for an advisor on configuration management.
    /// </summary>
    public interface IConfigManagerAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Asks if an arena.conf <paramref name="section"/> is restricted and can only be accessed or modified 
        /// by users with <see cref="Constants.Capabilities.AllowRestrictedSettings"/>.
        /// </summary>
        /// <param name="section">The section to check.</param>
        /// <returns><see langword="true"/> if the section is restricted; otherwise, <see langword="false"/>.</returns>
        bool IsArenaConfRestrictedSection(ReadOnlySpan<char> section) => false;

        /// <summary>
        /// Asks if a global.conf <paramref name="section"/> is restricted and can only be accessed or modified 
        /// by users with <see cref="Constants.Capabilities.AllowRestrictedSettings"/>.
        /// </summary>
        /// <param name="section">The section to check.</param>
        /// <returns><see langword="true"/> if the section is restricted; otherwise, <see langword="false"/>.</returns>
        bool IsGlobalConfRestrictedSection(ReadOnlySpan<char> section) => false;
    }
}

using System;

namespace SS.Core.ComponentAdvisors
{
    /// <summary>
    /// Interface for an advisor on configuration management.
    /// </summary>
    public interface IConfigManagerAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Asks if an arena.conf setting is restricted and can only be accessed or modified 
        /// by users with <see cref="Constants.Capabilities.AllowRestrictedSettings"/>.
        /// </summary>
        /// <remarks>
        /// A setting is considered to be restricted if any advisor says it is.
        /// </remarks>
        /// <param name="section">The section to check.</param>
        /// <returns><see langword="true"/> if the section is restricted; otherwise, <see langword="false"/>.</returns>
        bool IsArenaConfRestrictedSetting(ReadOnlySpan<char> section, ReadOnlySpan<char> key) => false;

        /// <summary>
        /// Asks if a global.conf setting is restricted and can only be accessed or modified 
        /// by users with <see cref="Constants.Capabilities.AllowRestrictedSettings"/>.
        /// </summary>
        /// <remarks>
        /// A setting is considered to be restricted if any advisor says it is.
        /// </remarks>
        /// <param name="section">The section to check.</param>
        /// <returns><see langword="true"/> if the section is restricted; otherwise, <see langword="false"/>.</returns>
        bool IsGlobalConfRestrictedSetting(ReadOnlySpan<char> section, ReadOnlySpan<char> key) => false;
    }
}

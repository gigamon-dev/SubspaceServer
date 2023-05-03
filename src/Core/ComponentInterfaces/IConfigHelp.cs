using System;
using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// A <see cref="ConfigHelpAttribute"/> and the module it came from.
    /// </summary>
    /// <param name="Attribute">Attribute containing info about a config setting.</param>
    /// <param name="Module">The module the <paramref name="Attribute"/> is in. <see langword="null"/> for attributes not in a module.</param>
    public readonly record struct ConfigHelpRecord(ConfigHelpAttribute Attribute, Type Module);

    /// <summary>
    /// Interface for getting information about config settings.
    /// </summary>
    public interface IConfigHelp : IComponentInterface
    {
        /// <summary>
        /// Locks the config help data for reading.
        /// </summary>
        void Lock();

        /// <summary>
        /// Unlocks the config help data for reading.
        /// </summary>
        void Unlock();

        /// <summary>
        /// Gets a list of known config sections.
        /// </summary>
        /// <remarks>
        /// Remember to use <see cref="Lock"/> and <see cref="Unlock"/>.
        /// </remarks>
        IReadOnlyList<string> Sections { get; }

        /// <summary>
        /// Gets keys for a config section.
        /// </summary>
        /// <remarks>
        /// Remember to use <see cref="Lock"/> and <see cref="Unlock"/>.
        /// </remarks>
        /// <param name="section">The section to get key for.</param>
        /// <param name="keyList">When this method returns, a list containing the keys in the section.</param>
        /// <returns><see langword="true"/> if information was found. Otherwise, <see langword="false"/>.</returns>
        bool TryGetSectionKeys(ReadOnlySpan<char> section, out IReadOnlyList<string> keyList);

        /// <summary>
        /// Gets help information about a setting.
        /// </summary>
        /// <remarks>
        /// Remember to use <see cref="Lock"/> and <see cref="Unlock"/>.
        /// </remarks>
        /// <param name="section">The section of the setting.</param>
        /// <param name="key">The key of the setting.</param>
        /// <param name="helpList">A list of help records, one for each <see cref="ConfigHelpAttribute"/>..</param>
        /// <returns><see langword="true"/> if information was found. Otherwise, <see langword="false"/>.</returns>
        bool TryGetSettingHelp(ReadOnlySpan<char> section, ReadOnlySpan<char> key, out IReadOnlyList<ConfigHelpRecord> helpList);
    }
}

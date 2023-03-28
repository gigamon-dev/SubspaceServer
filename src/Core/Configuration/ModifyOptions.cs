using System;

namespace SS.Core.Configuration
{
    [Flags]
    public enum ModifyOptions
    {
        /// <summary>
        /// No special options.
        /// <para>
        /// If the setting is an existing one, the existing property will be modified.
        /// Otherwise, if the section already exists, the setting will be added as a new property at the end of the existing section.
        /// Otherwise, the setting will be added as an override to the base conf file.
        /// </para>
        /// <para>
        /// If a comment is provided, it will replace any existing comments.
        /// If no comment is provided, any existing comments will be left alone.
        /// </para>
        /// </summary>
        None = 0x00,

        /// <summary>
        /// Only append to end of the base conf file with an override.
        /// </summary>
        AppendOverrideOnly = 0x01,

        /// <summary>
        /// Append the comment to the end of any existing comments instead of replacing them.
        /// </summary>
        LeaveExistingComments = 0x02,
    }
}

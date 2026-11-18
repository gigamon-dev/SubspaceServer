namespace SS.Core.Configuration
{
    /// <summary>
    /// The state of a <see cref="ConfFile"/> on disk, compared to what was loaded.
    /// </summary>
    public enum ConfFileDiskState
    {
        /// <summary>
        /// The file on disk has not been modified since it was loaded or saved.
        /// Also used for a <see cref="ConfFile"/> that is not yet saved to disk.
        /// </summary>
        Unchanged,

        /// <summary>
        /// The file on disk was modified since it was loaded or saved, and needs to be reloaded.
        /// </summary>
        Modified,

        /// <summary>
        /// The file no longer exists on disk (e.g., it, or the folder it was in, was deleted or renamed).
        /// </summary>
        Missing,
    }
}

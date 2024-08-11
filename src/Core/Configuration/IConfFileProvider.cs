namespace SS.Core.Configuration
{
    /// <summary>
    /// Interface for a service that provides <see cref="ConfFile"/> objects by name.
    /// The service locates and loads <see cref="ConfFile"/> objects.
    /// When doing so, it may choose to cache them too.
    /// </summary>
    /// <remarks>
    /// For locating the files, each implementation controls its own file search paths.
    /// An arena-based implementation may search arena folders first, followed by a global config folder.
    /// </remarks>
    public interface IConfFileProvider
    {
        public ConfFile? GetFile(string? name);
    }
}

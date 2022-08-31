namespace SS.Core.ComponentInterfaces
{
    public interface IHelp : IComponentInterface
    {
        /// <summary>
        /// The command name for getting help about other commands or configuration settings.
        /// </summary>
        string HelpCommand { get; }
    }
}

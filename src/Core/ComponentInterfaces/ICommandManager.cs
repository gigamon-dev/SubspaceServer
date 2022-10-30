using System;

namespace SS.Core.ComponentInterfaces
{
    [Flags]
    public enum CommandTarget
    {
        None = 1,
        Player = 2,
        Team = 4,
        Arena = 8,
        Any = 15,
    }

    /// <summary>
    /// Attribute for providing help information about a command.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public class CommandHelpAttribute : Attribute
    {
        /// <summary>
        /// Use this to specify the command the help information is for (for commands that share a common handler method with multiple <see cref="CommandHelpAttribute"/>s).
        /// </summary>
        public string Command { get; set; }

        /// <summary>
        /// Use this to specify the target or targets E.g., <code>CommandTarget.Player | CommandTarget.Team</code>
        /// </summary>
        public CommandTarget Targets { get; set; }

        /// <summary>
        /// Use this to describe the arguments the command accepts.
        /// </summary>
        public string Args { get; set; }

        /// <summary>
        /// Use this to describe what the command does and how the <see cref="Args"/> affect it.
        /// </summary>
        public string Description { get; set; }
    }

    /// <summary>
    /// Delegate for a command handler.
    /// </summary>
    /// <param name="commandName">The name of the command that was issued.</param>
    /// <param name="parameters">The characters the player typed after the command name.</param>
    /// <param name="p">The player issuing the command.</param>
    /// <param name="target">The target of the command.</param>
    public delegate void CommandDelegate(string commandName, string parameters, Player p, ITarget target);

    /// <summary>
    /// Delegate for a command handler that also uses sounds.
    /// </summary>
    /// <param name="commandName">The name of the command that was issued.</param>
    /// <param name="parameters">The characters the player typed after the command name.</param>
    /// <param name="player">The player issuing the command.</param>
    /// <param name="target">The target of the command.</param>
    /// <param name="sound">The sound to use.</param>
    public delegate void CommandWithSoundDelegate(string commandName, string parameters, Player player, ITarget target, ChatSound sound);

    /// <summary>
    /// Delegate for a 'default' command handler.
    /// </summary>
    /// <param name="commandName">The name of the command that was issued.</param>
    /// <param name="line">The full command line.</param>
    /// <param name="player">The player issuing the command.</param>
    /// <param name="target">The target of the command.</param>
    public delegate void DefaultCommandDelegate(ReadOnlySpan<char> commandName, ReadOnlySpan<char> line, Player player, ITarget target);

    /// <summary>
    /// Interface for a module which deals with registering and dispatching commands.
    /// 
    /// <para>
    /// There is no difference between ? commands and * commands. All
    /// commands (except of course commands handled on the client) work
    /// equally whether a ? or a * was used.
    /// </para>
    /// 
    /// <para>
    /// <see cref="ITarget"/> is used to describe how a command was issued.
    /// Commands typed as public chat messages have an <see cref="ITarget.Type"/> of <see cref="TargetType.Arena"/>.
    /// Commands typed as local private messages or remote private messages
    /// to another player on the same server but in a different arena get <see cref="TargetType.Player"/>, and
    /// commands sent as team messages (to your own team or an enemy team)
    /// get <see cref="TargetType.Freq"/>.
    /// </para>
    /// 
    /// <para>
    /// Help information about a command can be added using the <see cref="CommandHelpAttribute"/> on handler methods.
    /// If a handler method is used for multiple commands, use <see cref="CommandHelpAttribute.Command"/> to differentiate.
    /// </para>
    ///
    /// <para>
    /// Billing server modules can subscribe to <see cref="DefaultCommandReceived"/> to receive "default" commands.
    /// A 'default' command is a command that are not handled by the zone server 
    /// because no commands known to the zone server match a typed command
    /// or a command is purposely being skipped and being forwarded to billing.
    /// </para>
    /// </summary>
    public interface ICommandManager : IComponentInterface
    {
        /// <summary>
        /// Registers a command handler.
        /// Be sure to use <see cref="RemoveCommand"/> to unregister this before unloading.
        /// </summary>
        /// <param name="commandName">The name of the command to register.</param>
        /// <param name="handler">The command handler.</param>
        /// <param name="arena">Arena to register the command for. <see langword="null"/> for all arenas.</param>
        /// <param name="helpText">
        /// Help text for this command, or <see langword="null"/> for none.
        /// If <see langword="null"/>, it will look for a <see cref="CommandHelpAttribute"/> on the <paramref name="handler"/>.
        /// </param>
        void AddCommand(string commandName, CommandDelegate handler, Arena arena = null, string helpText = null);

        /// <summary>
        /// Registers a command handler.
        /// Be sure to use <see cref="RemoveCommand"/> to unregister this before unloading.
        /// </summary>
        /// <param name="commandName">The name of the command to register.</param>
        /// <param name="handler">The command handler.</param>
        /// <param name="arena">Arena to register the command for. <see langword="null"/> for all arenas.</param>
        /// <param name="helpText">
        /// Help text for this command, or <see langword="null"/> for none.
        /// If <see langword="null"/>, it will look for a <see cref="CommandHelpAttribute"/> on the <paramref name="handler"/>.
        /// </param>
        void AddCommand(string commandName, CommandWithSoundDelegate handler, Arena arena = null, string helpText = null);

        /// <summary>
        /// Unregisters a command handler.
        /// Use this to unregister handlers registered with <see cref="AddCommand"/>.
        /// </summary>
        /// <param name="commandName">The name of the command to unregister.</param>
        /// <param name="handler">The command handler.</param>
        /// <param name="arena">Arena to unregister the command for. <see langword="null"/> for all arenas.</param>
        void RemoveCommand(string commandName, CommandDelegate handler, Arena arena = null);

        /// <summary>
        /// Unregisters a command handler.
        /// Use this to unregister handlers registered with <see cref="AddCommand"/>.
        /// </summary>
        /// <param name="commandName">The name of the command to unregister.</param>
        /// <param name="handler">The command handler.</param>
        /// <param name="arena">Arena to unregister the command for. <see langword="null"/> for all arenas.</param>
        void RemoveCommand(string commandName, CommandWithSoundDelegate handler, Arena arena = null);

        /// <summary>
        /// Event for billing modules to subscribe to for them to handle 'default' commands.
        /// This provides a hook in so that billing modules may forward commands to a billing server to handle.
        /// </summary>
        event DefaultCommandDelegate DefaultCommandReceived;

        /// <summary>
        /// Dispatches an incoming command.
        /// This is generally only called by the chat module and billing server modules.
        /// If the first character of <paramref name="typedLine"/> is a backslash, command
        /// handlers in the server will be bypassed and the command will be
        /// passed directly to the default handler.
        /// </summary>
        /// <param name="typedLine">The text the player typed (without the ? in the beginning).</param>
        /// <param name="player">The player who issued the command.</param>
        /// <param name="target">The target of the command.</param>
        /// <param name="sound">The sound from the chat packet that this command came from.</param>
        void Command(ReadOnlySpan<char> typedLine, Player player, ITarget target, ChatSound sound);

        /// <summary>
        /// To get the help text of a command that was added.
        /// </summary>
        /// <param name="commandName"></param>
        /// <param name="arena"></param>
        /// <returns></returns>
        string GetHelpText(string commandName, Arena arena);

        /// <summary>
        /// Adds a command from the collection of commands that should not have its parameters be included in logs.
        /// It will still log that the command was executed, but without parameter details.
        /// </summary>
        /// <param name="commandName">The command to add.</param>
        void AddUnlogged(string commandName);

        /// <summary>
        /// Removes a command from the collection of commands that should not have its parameters be included in logs.
        /// </summary>
        /// <param name="commandName">The command to remove.</param>
        void RemoveUnlogged(string commandName);
    }
}

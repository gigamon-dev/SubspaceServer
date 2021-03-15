using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
    /// 
    /// </summary>
    /// <param name="command">the name of the command that was issued</param>
    /// <param name="parameters">the stuff that the player typed after the command name</param>
    /// <param name="p">the player issuing the command</param>
    /// <param name="target">describes how the command was issued (public, private, etc..</param>
    public delegate void CommandDelegate(string command, string parameters, Player p, ITarget target);

    /// <summary>
    /// the command manager; deals with registering and dispatching commands.
    /// 
    /// command handlers come in two flavors, which differ only in whether
    /// the handler gets to see the command name that was used. this can only
    /// make a difference if the same handler is used for multiple commands,
    /// of course. also, if you want to register per-arena commands, you need
    /// to use the second flavor. all handlers may assume p and p->arena are
    /// non-NULL.
    /// 
    /// Target structs are used to describe how a command was issued.
    /// commands typed as public chat messages get targets of type T_ARENA.
    /// commands typed as local private messages or remove private messages
    /// to another player on the same server get T_PLAYER targets, and
    /// commands sent as team messages (to your own team or an enemy team)
    /// get T_FREQ targets.
    /// 
    /// there is no difference between ? commands and * commands. all
    /// commands (except of course commands handled on the client) work
    /// equally whether a ? or a * was used.
    /// 
    /// help text should follow a standard format:
    /// <code>
    /// local helptext_t help_foo =
    /// "Module: ...\n"
    /// "Targets: ...\n"
    /// "Args: ...\n"
    /// More text here...\n";
    /// </code>
    ///
    /// the server keeps track of a "default" command handler, which will get
    /// called if no commands know to the server match a typed command. to
    /// set or remove the default handler, pass NULL as cmdname to any of the
    /// Add/RemoveCommand functions. this feature should only be used by
    /// billing server modules.
    /// </summary>
    public interface ICommandManager : IComponentInterface
    {
        /// <summary>
        /// Registers a command handler.
        /// Be sure to use RemoveCommand to unregister this before unloading.
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
        /// Unregisters a command handler.
        /// Use this to unregister handlers registered with AddCommand.
        /// </summary>
        /// <param name="commandName">The name of the command to unregister.</param>
        /// <param name="handler">The command handler.</param>
        /// <param name="arena">Arena to unregister the command for. <see langword="null"/> for all arenas.</param>
        void RemoveCommand(string commandName, CommandDelegate handler, Arena arena = null);

        /// <summary>
        /// Dispatches an incoming command.
        /// This is generally only called by the chat module and billing server modules.
        /// If the first character of typedline is a backslash, command
        /// handlers in the server will be bypassed and the command will be
        /// passed directly to the default handler.
        /// </summary>
        /// <param name="typedLine">what the player typed</param>
        /// <param name="p">the player who issued the command</param>
        /// <param name="target">how the command was issued</param>
        /// <param name="sound">the sound from the chat packet that this command came from</param>
        void Command(string typedLine, Player p, ITarget target, ChatSound sound);

        /// <summary>
        /// To get the help text of a command that was added.
        /// </summary>
        /// <param name="commandName"></param>
        /// <param name="arena"></param>
        /// <returns></returns>
        string GetHelpText(string commandName, Arena arena);
    }
}

using System;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="CommandExecutedDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class CommandExecutedCallback
    {
        /// <summary>
        /// Delegate for when a player executes a command.
        /// </summary>
        /// <param name="player">The player that executed the command.</param>
        /// <param name="target">The target of the command.</param>
        /// <param name="command">The command executed.</param>
        /// <param name="parameters">The command parameters. For unlogged commands, parameters are hidden and will be "...".</param>
        /// <param name="sound">The sound specified when running the command.</param>
        public delegate void CommandExecutedDelegate(Player player, ITarget target, ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, ChatSound sound);
    }
}

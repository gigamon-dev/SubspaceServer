using SS.Core.ComponentInterfaces;
using System;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="CommandExecutedDelegate"/> callback.
    /// </summary>
    public class CommandExecutedCallback
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

        public static void Register(IComponentBroker broker, CommandExecutedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, CommandExecutedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Player player, ITarget target, ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, ChatSound sound)
        {
            broker?.GetCallback<CommandExecutedDelegate>()?.Invoke(player, target, command, parameters, sound);

            if (broker?.Parent is not null)
                Fire(broker.Parent, player, target, command, parameters, sound);
        }
    }
}

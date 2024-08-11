using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that adds commands for players to notify staff members (e.g. ?cheater).
    /// </summary>
    [CoreModuleInfo]
    public class Notify : IModule
    {
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;

        private static readonly char[] _alertCommandDelimiters = { ' ', ',', ':', ';' };
        private readonly List<string> _commands = new();
        private string? _emptyReply;

        public Notify(
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        }

        #region Module members

        [ConfigHelp("Notify", "AlertCommand", ConfigScope.Global, typeof(string), DefaultValue = "cheater",
            Description = """
                A delimited list of commands that notify online staff.
                Allowed delimiters include: space, comma, colon, semicolon.
                """)]
        [ConfigHelp("Notify", "EmptyReply", ConfigScope.Global, typeof(string), DefaultValue = "",
            Description = "Reply to send when trying to send a Notify:AlertCommand without a message.")]
        bool IModule.Load(IComponentBroker broker)
        {
            string? delimitedCommands = _configManager.GetStr(_configManager.Global, "Notify", "AlertCommand");
            if (string.IsNullOrWhiteSpace(delimitedCommands))
                delimitedCommands = "cheater";

            foreach (string commandName in delimitedCommands.Split(_alertCommandDelimiters, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                _commands.Add(commandName.ToLowerInvariant());
                _commandManager.AddCommand(commandName, Command_NotifyCommand);
            }

            _emptyReply = _configManager.GetStr(_configManager.Global, "Notify", "EmptyReply");

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            foreach (string commandName in _commands)
            {
                _commandManager.RemoveCommand(commandName, Command_NotifyCommand);
            }

            _commands.Clear();

            return true;
        }

        #endregion

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<message>",
            Description = "Sends the message to all online staff members.")]
        private void Command_NotifyCommand(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena == null)
                return;

            if (_chat.GetPlayerChatMask(player).IsAllowed(ChatMessageType.ModChat))
            {
                if (parameters.IsWhiteSpace())
                {
                    _chat.SendMessage(player, !string.IsNullOrWhiteSpace(_emptyReply) ? _emptyReply : "Please include a message to send to online staff.");
                }
                else
                {
                    // same format as subgame
                    _chat.SendModMessage($"{commandName}: ({player.Name}) ({(arena.IsPublic ? "Public " : "")}{arena.Name}): {parameters}");
                    _chat.SendMessage(player, "Message has been sent to online staff.");
                }
            }
        }
    }
}

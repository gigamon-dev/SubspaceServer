using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.Modules
{
    public class Help : IModule
    {
        private IChat _chat;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;

        private string _helpCommandName;

        public bool Load(
            ComponentBroker broker, 
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager)
        {
            _chat = chat;
            _commandManager = commandManager;
            _configManager = configManager;

            _helpCommandName = _configManager.GetStr(_configManager.Global, "Help", "CommandName");
            if (string.IsNullOrWhiteSpace(_helpCommandName))
                _helpCommandName = "man";

            _commandManager.AddCommand(_helpCommandName, Command_help);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            _commandManager.RemoveCommand(_helpCommandName, Command_help);

            return true;
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<command name> | <setting name (section:key)>",
            Description =
            "Displays help on a command or config file setting. Use {section:}\n" +
            "to list known keys in that section. Use {:} to list known section\n" +
            "names.")]
        private void Command_help(string command, string parameters, Player p, ITarget target)
        {
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                if (parameters[0] == '?' || parameters[0] == '*' || parameters[0] == '!')
                {
                    parameters = parameters.Substring(1);
                }
            }

            if (string.IsNullOrWhiteSpace(parameters))
            {
                parameters = _helpCommandName;
            }

            if (parameters.Contains(':'))
            {
                // TODO: add help about settings
                _chat.SendMessage(p, $"Sorry, help about settings is not implemented yet.");
            }
            else
            {
                DoCommandHelp(p, parameters);
            }
        }

        private void DoCommandHelp(Player p, string command)
        {
            if (p == null)
                return;

            string helpText = _commandManager.GetHelpText(command, p.Arena);

            if (string.IsNullOrWhiteSpace(helpText))
            {
                _chat.SendMessage(p, $"Sorry, I don't know anything about '?{command}'.");
                return;
            }

            _chat.SendMessage(p, $"Help on '?{command}':");
            foreach (string str in helpText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                _chat.SendMessage(p, str);
            }
        }
    }
}

using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SS.Core.Modules
{
    public class Help : IModule, IModuleLoaderAware
    {
        private IChat _chat;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;

        private string _helpCommandName;
        private ILookup<string, (ConfigHelpAttribute Attr, string ModuleTypeName)> _settingsLookup;
        private Dictionary<string, string> _sectionAllKeysDictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _sectionGroupsStr;

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

        bool IModuleLoaderAware.PostLoad(ComponentBroker broker)
        {
            LoadConfigHelp();
            return true;
        }

        bool IModuleLoaderAware.PreUnload(ComponentBroker broker)
        {
            return true;
        }

        private void LoadConfigHelp()
        {
            var helpAttributes =
                from assembly in AppDomain.CurrentDomain.GetAssemblies()
                let assemblyProductAttribute = assembly.GetCustomAttribute<AssemblyProductAttribute>()
                where assemblyProductAttribute != null && !assemblyProductAttribute.Product.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                from type in assembly.GetTypes()
                let typeAttributes = type.GetCustomAttributes<ConfigHelpAttribute>()
                let methodAttributes =
                    from method in type.GetRuntimeMethods()
                    from attr in method.GetCustomAttributes<ConfigHelpAttribute>()
                    select attr
                let fieldAttributes =
                    from field in type.GetRuntimeFields()
                    from attr in field.GetCustomAttributes<ConfigHelpAttribute>()
                    select attr
                let propertyAttributes =
                    from property in type.GetRuntimeProperties()
                    from attr in property.GetCustomAttributes<ConfigHelpAttribute>()
                    select attr
                let allAttributes = typeAttributes.Concat(methodAttributes).Concat(fieldAttributes).Concat(propertyAttributes)
                from attr in allAttributes
                let moduleType = typeof(IModule).IsAssignableFrom(type) ? type : null
                select (attr, moduleType?.FullName);

            _settingsLookup = helpAttributes.ToLookup(tuple => tuple.attr.Section, StringComparer.OrdinalIgnoreCase);

            StringBuilder sectionGroupsBuilder = new StringBuilder();
            foreach (var sectionGroup in _settingsLookup)
            {
                if (sectionGroupsBuilder.Length > 0)
                    sectionGroupsBuilder.Append(", ");

                sectionGroupsBuilder.Append(sectionGroup.Key);

                var keys = sectionGroup
                    .Select(tuple => tuple.Attr.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);

                StringBuilder allKeysBuilder = new StringBuilder();
                foreach (string key in keys)
                {
                    if (allKeysBuilder.Length > 0)
                        allKeysBuilder.Append(", ");

                    allKeysBuilder.Append(key);
                }
                _sectionAllKeysDictionary[sectionGroup.Key] = allKeysBuilder.ToString();
            }

            _sectionGroupsStr = sectionGroupsBuilder.ToString();
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

            int colonIndex = parameters.IndexOf(':');
            if (colonIndex != -1)
            {
                string section = parameters.Substring(0, colonIndex);
                string key = parameters.Substring(colonIndex + 1);

                if (string.IsNullOrWhiteSpace(section))
                {
                    PrintConfigSections(p);
                }
                else if (string.IsNullOrWhiteSpace(key))
                {
                    PrintConfigSectionKeys(p, section);
                }
                else
                {
                    PrintConfigHelp(p, section, key);
                }
            }
            else
            {
                PrintCommandHelp(p, parameters);
            }
        }

        private void PrintConfigSections(Player p)
        {
            _chat.SendMessage(p, "Known config file sections:");
            _chat.SendWrappedText(p, _sectionGroupsStr);
        }

        private void PrintConfigSectionKeys(Player p, string section)
        {
            if (!_sectionAllKeysDictionary.TryGetValue(section, out string allKeysStr))
            {
                _chat.SendMessage(p, $"I don't know anything about section {section}.");
                return;
            }

            _chat.SendMessage(p, $"Known keys in section {section}:");
            _chat.SendWrappedText(p, allKeysStr);
        }

        private void PrintConfigHelp(Player p, string section, string key)
        {
            if (!_settingsLookup.Contains(section))
            {
                _chat.SendMessage(p, $"I don't know anything about section {section}.");
                return;
            }

            var keys = _settingsLookup[section].Where(tuple => string.Equals(tuple.Attr.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!keys.Any())
            {
                _chat.SendMessage(p, $"I don't know anything about key {key}.");
                return;
            }

            foreach (var keyTuple in keys)
            {
                _chat.SendMessage(p, $"Help on setting {section}:{keyTuple.Attr.Key}:");
                
                if (!string.IsNullOrWhiteSpace(keyTuple.ModuleTypeName))
                    _chat.SendMessage(p, $"  Requires module: {keyTuple.ModuleTypeName}");

                _chat.SendMessage(p, $"  Scope: {keyTuple.Attr.Scope}");
                _chat.SendMessage(p, $"  Type: {keyTuple.Attr.Type.Name}");

                if (!string.IsNullOrWhiteSpace(keyTuple.Attr.Range))
                    _chat.SendMessage(p, $"  Range: {keyTuple.Attr.Range}");

                if (!string.IsNullOrWhiteSpace(keyTuple.Attr.DefaultValue))
                    _chat.SendMessage(p, $"  Default: {keyTuple.Attr.DefaultValue}");

                _chat.SendWrappedText(p, keyTuple.Attr.Description);
            }
        }

        private void PrintCommandHelp(Player p, string command)
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

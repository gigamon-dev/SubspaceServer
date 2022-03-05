using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides the help commaand, which by default is ?man.
    /// This allows users to get information about a command or config file setting.
    /// </summary>
    public class Help : IModule, IModuleLoaderAware, IConfigHelp
    {
        private IChat _chat;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private IObjectPoolManager _objectPoolManager;
        private InterfaceRegistrationToken<IConfigHelp> _iConfigHelpToken;

        private string _helpCommandName;
        private ILookup<string, (ConfigHelpAttribute Attr, string ModuleTypeName)> _settingsLookup;
        public ILookup<string, (ConfigHelpAttribute Attr, string ModuleTypeName)> Sections => _settingsLookup;
        private readonly Dictionary<string, string> _sectionAllKeysDictionary = new(StringComparer.OrdinalIgnoreCase);
        private string _sectionGroupsStr;

        public bool Load(
            ComponentBroker broker, 
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            IObjectPoolManager objectPoolManager)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            _helpCommandName = _configManager.GetStr(_configManager.Global, "Help", "CommandName");
            if (string.IsNullOrWhiteSpace(_helpCommandName))
                _helpCommandName = "man";

            _commandManager.AddCommand(_helpCommandName, Command_help);

            _iConfigHelpToken = broker.RegisterInterface<IConfigHelp>(this);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iConfigHelpToken) != 0)
                return false;

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
            static Type GetModuleType(Type type)
            {
                if (type == null)
                    return null;

                if (typeof(IModule).IsAssignableFrom(type))
                    return type;
                else if (type.DeclaringType != null)
                    return GetModuleType(type.DeclaringType);
                else
                    return null;
            }

            var helpAttributes =
                from assembly in AppDomain.CurrentDomain.GetAssemblies()
                let assemblyProductAttribute = assembly.GetCustomAttribute<AssemblyProductAttribute>()
                where assemblyProductAttribute != null && !assemblyProductAttribute.Product.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                from type in assembly.GetTypes()
                let typeAttributes = type.GetCustomAttributes<ConfigHelpAttribute>()
                let constructorAttributes =
                    from constructor in type.GetConstructors()
                    from attr in constructor.GetCustomAttributes<ConfigHelpAttribute>()
                    select attr
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
                let allAttributes = typeAttributes.Concat(constructorAttributes).Concat(methodAttributes).Concat(fieldAttributes).Concat(propertyAttributes)
                from attr in allAttributes
                let moduleType = GetModuleType(type)
                select (attr, moduleType?.FullName);

            _settingsLookup = helpAttributes.ToLookup(tuple => tuple.attr.Section, StringComparer.OrdinalIgnoreCase);

            StringBuilder sectionGroupsBuilder = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                foreach (var sectionGroup in _settingsLookup)
                {
                    if (sectionGroupsBuilder.Length > 0)
                        sectionGroupsBuilder.Append(", ");

                    sectionGroupsBuilder.Append(sectionGroup.Key);

                    var keys = sectionGroup
                        .Select(tuple => tuple.Attr.Key)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);

                    StringBuilder allKeysBuilder = _objectPoolManager.StringBuilderPool.Get();

                    try
                    {
                        foreach (string key in keys)
                        {
                            if (allKeysBuilder.Length > 0)
                                allKeysBuilder.Append(", ");

                            allKeysBuilder.Append(key);
                        }

                        _sectionAllKeysDictionary[sectionGroup.Key] = allKeysBuilder.ToString();
                    }
                    finally
                    {
                        _objectPoolManager.StringBuilderPool.Return(allKeysBuilder);
                    }
                }

                _sectionGroupsStr = sectionGroupsBuilder.ToString();
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sectionGroupsBuilder);
            }
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
                    parameters = parameters[1..];
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
                string key = parameters[(colonIndex + 1)..];

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

            foreach ((ConfigHelpAttribute attr, string moduleTypeName) in keys)
            {
                _chat.SendMessage(p, $"Help on setting {section}:{attr.Key}:");
                
                if (!string.IsNullOrWhiteSpace(moduleTypeName))
                    _chat.SendMessage(p, $"  Requires module: {moduleTypeName}");

                if(string.IsNullOrWhiteSpace(attr.FileName))
                    _chat.SendMessage(p, $"  Location: {attr.Scope}");
                else
                    _chat.SendMessage(p, $"  Location: {attr.Scope}, File: {attr.FileName}");

                _chat.SendMessage(p, $"  Type: {attr.Type.Name}");

                if (!string.IsNullOrWhiteSpace(attr.Range))
                    _chat.SendMessage(p, $"  Range: {attr.Range}");

                if (!string.IsNullOrWhiteSpace(attr.DefaultValue))
                    _chat.SendMessage(p, $"  Default: {attr.DefaultValue}");

                _chat.SendWrappedText(p, attr.Description);
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

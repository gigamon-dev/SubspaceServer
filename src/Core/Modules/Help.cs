using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities.Collections;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides the help information about commands and config settings.
    /// <para>
    /// This module adds a help command, which by default is ?man.
    /// It allows users to get information about a command or config file setting.
    /// </para>
    /// <para>
    /// This module reads config setting information from <see cref="ConfigHelpAttribute"/>s and 
    /// makes that data accessible though the <see cref="IConfigHelp"/> interface.
    /// </para>
    /// </summary>
    [CoreModuleInfo]
    public sealed class Help : IModule, IModuleLoaderAware, IHelp, IConfigHelp, IDisposable
    {
        private IChat _chat;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IObjectPoolManager _objectPoolManager;
        private InterfaceRegistrationToken<IHelp> _iHelpToken;
        private InterfaceRegistrationToken<IConfigHelp> _iConfigHelpToken;

        private string _helpCommandName;

        /// <summary>
        /// ConfigHelpRecords for each assembly.
        /// </summary>
        private readonly Dictionary<Assembly, List<ConfigHelpRecord>> _assemblyConfigHelpDictionary = new();

        /// <summary>
        /// ConfigHelpRecords for each setting.
        /// </summary>
        /// <remarks>
        /// <para>Key = section:key</para>
        /// <para>Can be searched by section: or by section:key.</para>
        /// </remarks>
        private readonly Trie<List<ConfigHelpRecord>> _configHelpTrie = new(false);

        /// <summary>
        /// Known sections.
        /// </summary>
        private readonly List<string> _sectionList = new();

        /// <summary>
        /// Known keys for each known section.
        /// </summary>
        /// <remarks>
        /// Key = section,
        /// Value = list of known keys
        /// </remarks>
        private readonly Trie<List<string>> _sectionKeysTrie = new(false);

        // For use by the writer (there can only be one at a given time).
        private readonly SortedSet<string> _sortedSet = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Lock for synchronizing access to <see cref="_assemblyConfigHelpDictionary"/>, <see cref="_configHelpTrie"/>, <see cref="_sectionList"/>, and <see cref="_sectionKeysTrie"/>.
        /// </summary>
        private readonly ReaderWriterLockSlim _rwLock = new();

        /// <summary>
        /// Queue of config help work.
        /// </summary>
        /// <remarks>
        /// Items are the assembly to load config help for. <see langword="null"/> means load config help for all loaded assemblies.
        /// </remarks>
        private readonly Queue<Assembly> _loadQueue = new(16);

        /// <summary>
        /// The task that loads the config help. <see langword="null"/> means there is no loading in progress.
        /// </summary>
        private Task _loadTask = null;

        /// <summary>
        /// Lock for synchronizing access to <see cref="_loadQueue"/> and <see cref="_loadTask"/>.
        /// </summary>
        private readonly object _loadLock = new();

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            PluginAssemblyLoadedCallback.Register(broker, Callback_PluginAssemblyLoaded);
            PluginAssemblyUnloadingCallback.Register(broker, Callback_PluginAssemblyUnloading);

            _helpCommandName = _configManager.GetStr(_configManager.Global, "Help", "CommandName");
            if (string.IsNullOrWhiteSpace(_helpCommandName))
                _helpCommandName = "man";

            _commandManager.AddCommand(_helpCommandName, Command_help);

            _iHelpToken = broker.RegisterInterface<IHelp>(this);
            _iConfigHelpToken = broker.RegisterInterface<IConfigHelp>(this);

            return true;
        }

        bool IModuleLoaderAware.PostLoad(ComponentBroker broker)
        {
            lock (_loadLock)
            {
                // Add a job to process all loaded assemblies.
                _loadQueue.Enqueue(null);

                // Start a task to do the loading if there isn't already one.
                _loadTask ??= Task.Run(ProcessConfigHelpLoadJobs);
            }

            return true;
        }

        bool IModuleLoaderAware.PreUnload(ComponentBroker broker)
        {
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iHelpToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iConfigHelpToken) != 0)
                return false;

            _commandManager.RemoveCommand(_helpCommandName, Command_help);

            PluginAssemblyLoadedCallback.Unregister(broker, Callback_PluginAssemblyLoaded);
            PluginAssemblyUnloadingCallback.Unregister(broker, Callback_PluginAssemblyUnloading);


            Task loadTask;

            do
            {
                lock (_loadLock)
                {
                    loadTask = _loadTask;

                    if (loadTask is null)
                    {
                        // Unload all config help data.
                        _rwLock.EnterWriteLock();

                        try
                        {
                            _assemblyConfigHelpDictionary.Clear();
                            _configHelpTrie.Clear();
                            _sectionList.Clear();
                            _sectionKeysTrie.Clear();
                        }
                        finally
                        {
                            _rwLock.ExitWriteLock();
                        }
                    }
                }

                loadTask?.Wait();
            }
            while (loadTask is not null);

            return true;
        }

        #endregion

        #region IHelp

        string IHelp.HelpCommand => _helpCommandName;

        #endregion

        #region IConfigHelp

        void IConfigHelp.Lock()
        {
            _rwLock.EnterReadLock();
        }

        void IConfigHelp.Unlock()
        {
            _rwLock.ExitReadLock();
        }

        IReadOnlyList<string> IConfigHelp.Sections
        {
            get
            {
                if (!_rwLock.IsReadLockHeld)
                    throw new InvalidOperationException($"{nameof(IConfigHelp)}.{nameof(IConfigHelp.Lock)} was not called.");

                return _sectionList;
            }
        }

        bool IConfigHelp.TryGetSectionKeys(ReadOnlySpan<char> section, out IReadOnlyList<string> keyList)
        {
            if (!_rwLock.IsReadLockHeld)
                throw new InvalidOperationException($"{nameof(IConfigHelp)}.{nameof(IConfigHelp.Lock)} was not called.");

            if (!_sectionKeysTrie.TryGetValue(section, out List<string> keys))
            {
                keyList = null;
                return false;
            }

            keyList = keys;
            return true;
        }

        bool IConfigHelp.TryGetSettingHelp(ReadOnlySpan<char> section, ReadOnlySpan<char> key, out IReadOnlyList<ConfigHelpRecord> helpList)
        {
            if (!_rwLock.IsReadLockHeld)
                throw new InvalidOperationException($"{nameof(IConfigHelp)}.{nameof(IConfigHelp.Lock)} was not called.");

            Span<char> sectionKey = stackalloc char[section.Length + 1 + key.Length];
            if (!sectionKey.TryWrite($"{section}:{key}", out int charsWritten)
                || charsWritten != sectionKey.Length)
            {
                helpList = null;
                return false;
            }

            if (!_configHelpTrie.TryGetValue(sectionKey, out List<ConfigHelpRecord> records))
            {
                helpList = null;
                return false;
            }

            helpList = records;
            return true;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _rwLock.Dispose();
        }

        #endregion

        #region Callbacks

        private void Callback_PluginAssemblyLoaded(Assembly assembly)
        {
            if (assembly is null)
                return;

            lock (_loadLock)
            {
                // Add a job to process the assembly.
                _loadQueue.Enqueue(assembly);

                // Start a task to do the loading if there isn't already one.
                _loadTask ??= Task.Run(ProcessConfigHelpLoadJobs);
            }
        }

        private void Callback_PluginAssemblyUnloading(Assembly assembly)
        {
            if (assembly is null)
                return;

            Task loadTask;

            do
            {
                lock (_loadLock)
                {
                    loadTask = _loadTask;

                    if (loadTask is null)
                    {
                        // Unload config help data for the assembly.
                        _rwLock.EnterWriteLock();

                        try
                        {
                            RemoveConfigHelp(assembly);
                            RefreshKnownSectionsAndKeys();
                        }
                        finally
                        {
                            _rwLock.ExitWriteLock();
                        }
                    }
                }

                loadTask?.Wait();
            }
            while (loadTask is not null);
        }

        #endregion

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<command name> | <setting name (section:key)>",
            Description = """
                Displays help on a command or config file setting. Use {section:}
                to list known keys in that section. Use {:} to list known section
                names.
                """)]
        private void Command_help(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            parameters = parameters.Trim();
            if (!parameters.IsEmpty)
            {
                if (parameters[0] == '?' || parameters[0] == '*' || parameters[0] == '!')
                {
                    parameters = parameters[1..].TrimStart();
                }
            }

            if (parameters.IsEmpty)
            {
                parameters = _helpCommandName;
            }

            int colonIndex = parameters.IndexOf(':');
            if (colonIndex != -1)
            {
                ReadOnlySpan<char> section = parameters[..colonIndex].Trim();
                ReadOnlySpan<char> key = parameters[(colonIndex + 1)..].Trim();

                if (section.IsEmpty)
                {
                    PrintConfigSections(player);
                }
                else if (key.IsEmpty)
                {
                    PrintConfigSectionKeys(player, section);
                }
                else
                {
                    PrintConfigHelp(player, section, key);
                }
            }
            else
            {
                PrintCommandHelp(player, parameters);
            }


            void PrintConfigSections(Player player)
            {
                StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                try
                {
                    _rwLock.EnterReadLock();

                    try
                    {
                        foreach (string section in _sectionList)
                        {
                            if (sb.Length > 0)
                                sb.Append(", ");

                            sb.Append(section);
                        }
                    }
                    finally
                    {
                        _rwLock.ExitReadLock();
                    }

                    _chat.SendMessage(player, "Known config file sections:");
                    _chat.SendWrappedText(player, sb);
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(sb);
                }
            }

            void PrintConfigSectionKeys(Player player, ReadOnlySpan<char> section)
            {
                StringBuilder sb = null;

                try
                {
                    _rwLock.EnterReadLock();

                    try
                    {
                        if (!_sectionKeysTrie.TryGetValue(section, out List<string> keyList))
                        {
                            _chat.SendMessage(player, $"Config file section '{section}' not found.");
                            return;
                        }

                        sb = _objectPoolManager.StringBuilderPool.Get();

                        foreach (string key in keyList)
                        {
                            if (sb.Length > 0)
                                sb.Append(", ");

                            sb.Append(key);
                        }
                    }
                    finally
                    {
                        _rwLock.ExitReadLock();
                    }

                    _chat.SendMessage(player, $"Known keys in config file section '{section}':");
                    _chat.SendWrappedText(player, sb);
                }
                finally
                {
                    if (sb is not null)
                    {
                        _objectPoolManager.StringBuilderPool.Return(sb);
                    }
                }
            }

            void PrintConfigHelp(Player player, ReadOnlySpan<char> section, ReadOnlySpan<char> key)
            {
                if (section.IsEmpty || key.IsEmpty)
                    return;

                Span<char> sectionKey = stackalloc char[section.Length + 1 + key.Length];
                if (!sectionKey.TryWrite($"{section}:{key}", out int charsWritten)
                    || charsWritten != sectionKey.Length)
                {
                    return;
                }

                _rwLock.EnterReadLock();

                try
                {
                    if (!_configHelpTrie.TryGetValue(sectionKey, out List<ConfigHelpRecord> records))
                    {
                        _chat.SendMessage(player, $"Config file setting '{section}:{key}' not found.");
                        return;
                    }

                    foreach ((ConfigHelpAttribute attribute, Type moduleType) in records)
                    {
                        _chat.SendMessage(player, $"Help on setting '{attribute.Section}:{attribute.Key}':");

                        if (moduleType is not null)
                            _chat.SendMessage(player, $"  Requires module: {moduleType.FullName}");

                        if (string.IsNullOrWhiteSpace(attribute.FileName))
                            _chat.SendMessage(player, $"  Location: {attribute.Scope}");
                        else
                            _chat.SendMessage(player, $"  Location: {attribute.Scope}, File: {attribute.FileName}");

                        _chat.SendMessage(player, $"  Type: {attribute.Type.Name}");

                        if (!string.IsNullOrWhiteSpace(attribute.Range))
                            _chat.SendMessage(player, $"  Range: {attribute.Range}");

                        if (!string.IsNullOrWhiteSpace(attribute.DefaultValue))
                            _chat.SendMessage(player, $"  Default: {attribute.DefaultValue}");

                        if (attribute.Description.Contains('\n'))
                        {
                            ReadOnlySpan<char> remaining = attribute.Description;

                            while (!remaining.IsEmpty)
                            {
                                ReadOnlySpan<char> line;

                                int index = remaining.IndexOf('\n');
                                if (index == -1)
                                {
                                    line = remaining;
                                    remaining = ReadOnlySpan<char>.Empty;
                                }
                                else
                                {
                                    line = remaining[..index];
                                    remaining = remaining[(index + 1)..];
                                }

                                line = line.TrimEnd("\r");

                                _chat.SendMessage(player, $"  {line}");
                            }
                        }
                        else
                        {
                            _chat.SendWrappedText(player, attribute.Description);
                        }
                    }
                }
                finally
                {
                    _rwLock.ExitReadLock();
                }
            }

            void PrintCommandHelp(Player player, ReadOnlySpan<char> command)
            {
                if (player == null)
                    return;

                string helpText = _commandManager.GetHelpText(command, player.Arena);

                if (string.IsNullOrWhiteSpace(helpText))
                {
                    _chat.SendMessage(player, $"Command '?{command}' not found.");
                    return;
                }

                _chat.SendMessage(player, $"Help on command '?{command}':");

                ReadOnlySpan<char> remaining = helpText;

                while (!remaining.IsEmpty)
                {
                    ReadOnlySpan<char> line;

                    int index = remaining.IndexOf('\n');
                    if (index == -1)
                    {
                        line = remaining;
                        remaining = ReadOnlySpan<char>.Empty;
                    }
                    else
                    {
                        line = remaining[..index];
                        remaining = remaining[(index + 1)..];
                    }

                    line = line.TrimEnd("\r");
                    if (line.IsEmpty)
                        line = " "; // clients do not display empty lines, so include a space so that it looks like an empty line

                    _chat.SendMessage(player, line);
                }
            }
        }

        private void ProcessConfigHelpLoadJobs()
        {
            while (true)
            {
                Assembly assembly = null;

                lock (_loadLock)
                {
                    if (!_loadQueue.TryDequeue(out assembly))
                    {
                        _loadTask = null;
                        break;
                    }
                }

                _rwLock.EnterWriteLock();

                try
                {
                    if (assembly is not null)
                    {
                        Load(assembly);
                    }
                    else
                    {
                        foreach (Assembly otherAssembly in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            Load(otherAssembly);
                        }
                    }

                    RefreshKnownSectionsAndKeys();
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(Help), $"Error processing ConfigHelpAttributes. {ex}");
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }
            }


            void Load(Assembly assembly)
            {
                if (assembly is null)
                    return;

                if (_assemblyConfigHelpDictionary.ContainsKey(assembly))
                {
                    // Already loaded.
                    return;
                }

                try
                {
                    var assemblyProductAttribute = assembly.GetCustomAttribute<AssemblyProductAttribute>();
                    if (assemblyProductAttribute is null
                        || assemblyProductAttribute.Product.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    int count = 0;
                    foreach (Type type in assembly.GetTypes())
                    {
                        foreach (var constructorInfo in type.GetConstructors())
                        {
                            foreach (var attribute in constructorInfo.GetCustomAttributes<ConfigHelpAttribute>(false))
                            {
                                Add(attribute, assembly, type);
                                count++;
                            }
                        }

                        foreach (var methodInfo in type.GetRuntimeMethods())
                        {
                            foreach (var attribute in methodInfo.GetCustomAttributes<ConfigHelpAttribute>(false))
                            {
                                Add(attribute, assembly, type);
                                count++;
                            }
                        }

                        foreach (var fieldInfo in type.GetRuntimeFields())
                        {
                            foreach (var attribute in fieldInfo.GetCustomAttributes<ConfigHelpAttribute>(false))
                            {
                                Add(attribute, assembly, type);
                                count++;
                            }
                        }

                        foreach (var propertyInfo in type.GetRuntimeProperties())
                        {
                            foreach (var attribute in propertyInfo.GetCustomAttributes<ConfigHelpAttribute>(false))
                            {
                                Add(attribute, assembly, type);
                                count++;
                            }
                        }

                        foreach (var eventInfo in type.GetRuntimeEvents())
                        {
                            foreach (var attribute in eventInfo.GetCustomAttributes<ConfigHelpAttribute>(false))
                            {
                                Add(attribute, assembly, type);
                                count++;
                            }
                        }
                    }

                    if (count > 0)
                    {
                        _logManager.LogM(LogLevel.Info, nameof(Help), $"Read {count} {(count == 1 ? "attribute" : "attributes")} from assembly '{assembly}'.");
                    }
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(Help), $"Unable to read attributes from assembly '{assembly}'. {ex.Message}");
                }


                void Add(ConfigHelpAttribute attribute, Assembly assembly, Type type)
                {
                    if (attribute is null || assembly is null)
                        return;

                    if (string.IsNullOrWhiteSpace(attribute.Section) || string.IsNullOrWhiteSpace(attribute.Key))
                        return;

                    Span<char> sectionKey = stackalloc char[attribute.Section.Length + 1 + attribute.Key.Length];
                    if (!sectionKey.TryWrite($"{attribute.Section}:{attribute.Key}", out int charsWritten)
                        || charsWritten != sectionKey.Length)
                    {
                        return;
                    }

                    Type moduleType = GetModuleType(type);
                    ConfigHelpRecord record = new(attribute, moduleType);

                    AddAssemblyRecord(assembly, record);
                    AddSettingRecord(sectionKey, record);


                    static Type GetModuleType(Type type)
                    {
                        if (type is null)
                            return null;

                        if (typeof(IModule).IsAssignableFrom(type))
                            return type;
                        else if (type.DeclaringType != null)
                            return GetModuleType(type.DeclaringType);
                        else
                            return null;
                    }

                    void AddAssemblyRecord(Assembly assembly, ConfigHelpRecord record)
                    {
                        if (!_assemblyConfigHelpDictionary.TryGetValue(assembly, out List<ConfigHelpRecord> configHelpList))
                        {
                            configHelpList = new List<ConfigHelpRecord>();
                            _assemblyConfigHelpDictionary.Add(assembly, configHelpList);
                        }

                        configHelpList.Add(record);
                    }

                    void AddSettingRecord(Span<char> sectionKey, ConfigHelpRecord record)
                    {
                        if (!_configHelpTrie.TryGetValue(sectionKey, out List<ConfigHelpRecord> configHelpList))
                        {
                            configHelpList = new List<ConfigHelpRecord>(1); // each setting is usually represented by a single attribute
                            _configHelpTrie.Add(sectionKey, configHelpList);
                        }

                        configHelpList.Add(record);
                    }
                }
            }
        }

        private void RemoveConfigHelp(Assembly assembly)
        {
            if (assembly is null
                || !_assemblyConfigHelpDictionary.Remove(assembly, out List<ConfigHelpRecord> configHelpList))
            {
                return;
            }

            foreach (ConfigHelpRecord record in configHelpList)
            {
                RemoveSettingRecord(record);
            }

            configHelpList.Clear();


            void RemoveSettingRecord(ConfigHelpRecord record)
            {
                Span<char> sectionKey = stackalloc char[record.Attribute.Section.Length + 1 + record.Attribute.Key.Length];
                if (!sectionKey.TryWrite($"{record.Attribute.Section}:{record.Attribute.Key}", out int charsWritten)
                    || charsWritten != sectionKey.Length)
                {
                    return;
                }

                if (!_configHelpTrie.TryGetValue(sectionKey, out List<ConfigHelpRecord> configHelpList))
                    return;

                configHelpList.Remove(record);

                if (configHelpList.Count == 0)
                {
                    _configHelpTrie.Remove(sectionKey, out _);
                }
            }
        }

        private void RefreshKnownSectionsAndKeys()
        {
            // Known sections
            RefreshSections();

            // Known keys for each section
            RefreshKeys();


            void RefreshSections()
            {
                _sortedSet.Clear();

                foreach (var helpList in _configHelpTrie.Values)
                {
                    foreach (ConfigHelpRecord record in helpList)
                    {
                        _sortedSet.Add(record.Attribute.Section);
                    }
                }

                _sectionList.Clear();
                _sectionList.AddRange(_sortedSet);
            }

            void RefreshKeys()
            {
                // Clear each section's keys.
                foreach (List<string> keyList in _sectionKeysTrie.Values)
                {
                    keyList.Clear();
                }

                // Add keys for each section.
                foreach (string section in _sectionList)
                {
                    RefreshKeysForSection(section);
                }

                // Remove sections that no longer exist.
                while (TryRemoveOneEmpty()) { }


                bool TryRemoveOneEmpty()
                {
                    foreach (var (section, keyList) in _sectionKeysTrie)
                    {
                        if (keyList.Count == 0)
                        {
                            _sectionKeysTrie.Remove(section.Span, out _);
                            return true;
                        }
                    }

                    return false;
                }
            }

            void RefreshKeysForSection(string section)
            {
                _sortedSet.Clear();

                Span<char> search = stackalloc char[section.Length + 1];
                if (!search.TryWrite($"{section}:", out int charsWritten) || charsWritten != search.Length)
                    return;

                foreach ((_, var helpList) in _configHelpTrie.StartsWith(search))
                {
                    foreach (ConfigHelpRecord record in helpList)
                    {
                        _sortedSet.Add(record.Attribute.Key);
                    }
                }

                if (_sectionKeysTrie.TryGetValue(section, out List<string> keyList))
                {
                    keyList.Clear();
                    keyList.AddRange(_sortedSet);
                }
                else
                {
                    keyList = new List<string>(_sortedSet);
                    _sectionKeysTrie.Add(section, keyList);
                }
            }
        }
    }
}

using SS.Core.ComponentInterfaces;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;

namespace SS.Core
{
    /// <summary>
    /// Equivalent of ASSS' module.[ch]
    /// Completely different style though...
    /// 
    /// The <see cref="ModuleManager"/> is a specialized IoC container.
    /// 
    /// Modules are loaded based on their dependencies.
    /// If modules have cyclic dependencies, they simply will not be loaded.
    /// 
    /// Dependencies are actually on the interfaces.  That is, a module doesn't directly depend on other specific modules.
    /// Many times, a module will have a single interface that represents the module.  But, that is not always so.  A
    /// module can register itself, or an object that it manages, as being the implementor of an interface.
    /// A module can be loaded as long as the interfaces it requires to load have been registered.
    /// 
    /// Callbacks are publisher / subscriber AKA Pub/Sub.  There can be any # of publishers and any # of subcribers to a single Callback.
    /// A subscriber can subscribe even if there are no publishers yet.
    /// Each Callback is identified by a unique name.
    /// Therefore, Callbacks are not dependencies that affect whether a module can be loaded.
    /// 
    /// Interfaces and callbacks are usually registered when a module loads and unregistered when unloaded.
    /// However, it is not limited to doing that.  For example, at any time, a module can check if there's an implementor of an interface,
    /// and if so, get it, and use it.
    /// 
    /// An <see cref="Arena"/> is similar to a <see cref="ModuleManager"/> in that it also is a broker for Interfaces and Callbacks.
    /// The <see cref="ModuleManager"/> is simply the root (global) broker.  It it the parent container of all <see cref="Arena"/>s.
    /// Therefore both <see cref="ModuleManager"/> and <see cref="Arena"/> derive from <see cref="ComponentBroker"/>.
    /// However, only the <see cref="ModuleManager"/> manages loading/unloading of modules.
    /// Since attaching a module to an arena is part of the module loading lifecycle, the <see cref="ModuleManager"/> is aware
    /// of the existence of <see cref="Arena"/>s, even though it is not the one who directly manages them.  That is the job of
    /// the <see cref="Modules.ArenaManager"/>.
    /// </summary>
    [CoreModuleInfo]
    public sealed class ModuleManager : ComponentBroker, IModuleManager
    {
        /// <summary>
        /// For synchronizing access to all data members.
        /// </summary>
        private readonly object _moduleLockObj = new object();

        /// <summary>
        /// All registered (and possibly loaded) modules.
        /// </summary>
        private readonly Dictionary<Type, ModuleData> _moduleTypeLookup = new Dictionary<Type, ModuleData>();

        /// <summary>
        /// Modules that are loaded, in the order that they were loaded.
        /// </summary>
        private readonly LinkedList<Type> _loadedModules = new LinkedList<Type>();

        /// <summary>
        /// Assembly Path --> Assembly
        /// </summary>
        /// TODO: StringComparer based whether the file system is case sensitive? Also, how to handle a mixture of file systems?
        private readonly Dictionary<string, Assembly> _loadedPluginAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Types of 'plug-in' modules that registered.
        /// Plug-in modules are modules that are in assemblies loaded (and isolated) into their own <see cref="ModulePluginLoadContext"/> as a 'plug-in'.
        /// </summary>
        private readonly HashSet<Type> _pluginModuleTypeSet = new HashSet<Type>();

        /// <summary>
        /// Constructs a <see cref="ModuleManager"/>.
        /// </summary>
        public ModuleManager()
        {
            RegisterInterface<IModuleManager>(this);
        }

        #region Arena Attach/Detach

        public bool AttachModule(string moduleTypeName, Arena arena)
        {
            if (string.IsNullOrWhiteSpace(moduleTypeName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(moduleTypeName));

            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            Type type = Type.GetType(moduleTypeName);
            if (type != null)
            {
                return AttachModule(type, arena);
            }

            lock (_moduleLockObj)
            {
                IEnumerable<Type> types = GetPluginModuleTypes(moduleTypeName);
                bool success = false;
                bool failure = false;

                foreach (Type t in types)
                {
                    if (AttachModule(t, arena))
                        success = true;
                    else
                        failure = true;
                }

                if (success && !failure)
                    return true;
            }

            WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Unable to find module '{moduleTypeName}'.");
            return false;
        }

        public bool AttachModule(Type type, Arena arena)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!IsModule(type))
            {
                WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Type '{type.FullName}', but it is not a module.");
                return false;
            }

            lock (_moduleLockObj)
            {
                if (!_moduleTypeLookup.TryGetValue(type, out ModuleData moduleInfo))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' is not registered, it needs to be loaded first.");
                    return false;
                }

                if (!moduleInfo.IsLoaded)
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' is not loaded.");
                    return false;
                }

                if (moduleInfo.AttachedArenas.Contains(arena))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' is already attached to the arena.");
                    return false;
                }

                if (!(moduleInfo.Module is IArenaAttachableModule arenaAttachableModule))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' does not support attaching.");
                    return false;
                }

                if (!arenaAttachableModule.AttachModule(arena))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' failed to attach.");
                    return false;
                }

                moduleInfo.AttachedArenas.Add(arena);
                return true;
            }
        }

        public bool DetachModule(string moduleTypeName, Arena arena)
        {
            if (string.IsNullOrWhiteSpace(moduleTypeName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(moduleTypeName));

            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            Type type = Type.GetType(moduleTypeName);
            if (type != null)
            {
                return DetachModule(type, arena);
            }

            lock (_moduleLockObj)
            {
                IEnumerable<Type> types = GetPluginModuleTypes(moduleTypeName);
                bool success = false;
                bool failure = false;

                foreach (Type t in types)
                {
                    if (DetachModule(t, arena))
                        success = true;
                    else
                        failure = true;
                }

                if (success && !failure)
                    return true;
            }

            WriteLogA(LogLevel.Error, arena, $"DetachModule failed: Unable to find module '{moduleTypeName}'.");
            return false;
        }

        public bool DetachModule(Type type, Arena arena)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            if (!IsModule(type))
            {
                WriteLogA(LogLevel.Error, arena, $"DetachModule failed: Type '{type.FullName}' is not a module.");
                return false;
            }

            lock (_moduleLockObj)
            {
                if (!_moduleTypeLookup.TryGetValue(type, out ModuleData moduleInfo))
                {
                    WriteLogA(LogLevel.Error, arena, $"DetachModule failed: Module '{type.FullName}' is not registered.");
                    return false;
                }

                if (!moduleInfo.AttachedArenas.Contains(arena))
                {
                    WriteLogA(LogLevel.Error, arena, $"DetachModule failed: Module '{type.FullName}' is not attached to the arena.");
                    return false;
                }

                if (!(moduleInfo.Module is IArenaAttachableModule arenaAttachableModule))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' does not support attaching.");
                    return false;
                }

                if (!arenaAttachableModule.DetachModule(arena))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' failed to detach.");
                    return false;
                }

                moduleInfo.AttachedArenas.Remove(arena);
                return true;
            }
        }

        public bool DetachAllFromArena(Arena arena)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            bool ret = true;

            lock (_moduleLockObj)
            {
                foreach (var moduleInfo in _moduleTypeLookup.Values)
                {
                    if (moduleInfo.AttachedArenas.Contains(arena))
                    {
                        if (!DetachModule(moduleInfo.ModuleType, arena))
                            ret = false;
                    }
                }
            }

            return ret;
        }

        #endregion

        #region Add Module

        public bool AddModule(string moduleTypeName, string path) => AddAndLoadModule(moduleTypeName, path, false);

        /// <summary>
        /// Adds a module to be loaded later.
        /// </summary>
        /// <param name="moduleTypeName">The assembly to load in the format of <see cref="Type.AssemblyQualifiedName"/>.</param>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        public bool AddModule(string moduleTypeName) => AddAndLoadModule(moduleTypeName, null, false);

        /// <summary>
        /// Adds a module to be loaded later.
        /// </summary>
        /// <param name="module">The module to add.</param>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        public bool AddModule(IModule module) => AddAndLoadModule(module, false);

        /// <summary>
        /// Adds a module to be loaded later.
        /// </summary>
        /// <typeparam name="TModule">Type of the module to add.</typeparam>
        /// <param name="module">The module to add.</param>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        public bool AddModule<TModule>(TModule module) where TModule : class, IModule => AddAndLoadModule(module, false);

        /// <summary>
        /// Adds a module to be loaded later.
        /// </summary>
        /// <typeparam name="TModule">Type of the module to add.</typeparam>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        public bool AddModule<TModule>() where TModule : class, IModule => AddAndLoadModule<TModule>(false);

        #endregion

        #region Load Module

        public bool LoadModule(string moduleTypeName, string path) => AddAndLoadModule(moduleTypeName, path, true);

        /// <summary>
        /// Adds and loads a module.
        /// </summary>
        /// <param name="moduleTypeName">The assembly to load in the format of <see cref="Type.AssemblyQualifiedName"/>.</param>
        /// <returns>True if the module was added and loaded. Otherwise, false.</returns>
        public bool LoadModule(string moduleTypeName) => AddAndLoadModule(moduleTypeName, null, true);

        /// <summary>
        /// Adds and loads a module.
        /// </summary>
        /// <param name="module">The module to add and load.</param>
        /// <returns>True if the module was added and loaded. Otherwise, false.</returns>
        public bool LoadModule(IModule module) => AddAndLoadModule(module, true);

        /// <summary>
        /// Adds and loads a module.
        /// </summary>
        /// <typeparam name="TModule">Type of the module to add and load.</typeparam>
        /// <param name="module">The module to add and load.</param>
        /// <returns>True if the module was added and loaded. Otherwise, false.</returns>
        public bool LoadModule<TModule>(TModule module) where TModule : class, IModule => AddAndLoadModule(module, true);

        /// <summary>
        /// Adds and loads a module.
        /// </summary>
        /// <typeparam name="TModule">Type of the module to add and load.</typeparam>
        /// <returns>True if the module was added and loaded. Otherwise, false.</returns>
        public bool LoadModule<TModule>() where TModule : class, IModule => AddAndLoadModule<TModule>(true);

        #endregion

        #region Add / Load Helper methods

        private bool AddAndLoadModule(string moduleTypeName, string path, bool load)
        {
            if (string.IsNullOrWhiteSpace(moduleTypeName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(moduleTypeName));

            Type type = Type.GetType(moduleTypeName);
            if (type == null)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    type = GetTypeFromPluginAssemblyPath(moduleTypeName, path);
                }

                if (type == null)
                {
                    WriteLogM(LogLevel.Error, $"Unable to find module '{moduleTypeName}'.");
                    return false;
                }
            }

            IModule module = CreateModuleObject(type);
            if (module == null)
                return false;

            return AddAndLoadModule(type, module, load);
        }

        private bool AddAndLoadModule(IModule module, bool load)
        {
            if (module == null)
                throw new ArgumentNullException(nameof(module));

            Type moduleType = module.GetType();
            return AddAndLoadModule(moduleType, module, load);
        }

        private bool AddAndLoadModule<TModule>(TModule module, bool load) where TModule : class, IModule
        {
            if (module == null)
                throw new ArgumentNullException(nameof(module));

            Type moduleType = typeof(TModule);
            return AddAndLoadModule(moduleType, module, load);
        }

        private bool AddAndLoadModule<TModule>(bool load) where TModule : class, IModule
        {
            if (!(CreateModuleObject(typeof(TModule)) is TModule module))
                return false;

            return AddAndLoadModule(module, load);
        }

        private bool AddAndLoadModule(Type moduleType, IModule module, bool load)
        {
            if (moduleType == null)
                throw new ArgumentNullException(nameof(moduleType));

            if (module == null)
                throw new ArgumentNullException(nameof(module));

            lock (_moduleLockObj)
            {
                if (_moduleTypeLookup.ContainsKey(moduleType))
                    return false;

                ModuleData moduleInfo = new ModuleData(module);
                _moduleTypeLookup.Add(moduleType, moduleInfo);

                Assembly assembly = moduleType.Assembly;
                AssemblyLoadContext loadContext = AssemblyLoadContext.GetLoadContext(assembly);
                if (loadContext != AssemblyLoadContext.Default
                    && loadContext is ModulePluginLoadContext moduleLoadContext)
                {
                    _pluginModuleTypeSet.Add(moduleType);
                }

                if (load)
                    return LoadModule(moduleInfo);

                return true;
            }
        }

        private bool IsModule(Type type)
        {
            if (type == null)
                return false;

            // Verify it's a class.
            if (type.IsClass == false)
                return false;

            // Verify it implements the IModule interface.
            if (!typeof(IModule).IsAssignableFrom(type))
                return false;

            return true;
        }

        private IModule CreateModuleObject(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            if (IsModule(type) == false)
                return null;

            return Activator.CreateInstance(type) as IModule;
        }

        #endregion

        #region Unload Module

        /// <summary>
        /// Unloads a module.
        /// </summary>
        /// <param name="moduleTypeName">The type to unload in the format of <see cref="Type.AssemblyQualifiedName"/>.</param>
        /// <returns>True if the module was unloaded. Otherwise false.</returns>
        public bool UnloadModule(string moduleTypeName)
        {
            if (string.IsNullOrWhiteSpace(moduleTypeName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(moduleTypeName));

            Type type = Type.GetType(moduleTypeName);
            if (type != null)
                return UnloadModule(type);

            return UnloadPluginModule(moduleTypeName);
        }

        private bool UnloadPluginModule(string moduleTypeName)
        {
            lock (_moduleLockObj)
            {
                Type[] types = GetPluginModuleTypes(moduleTypeName).ToArray();
                bool success = false;
                bool failure = false;

                foreach (Type type in types)
                {
                    if (UnloadModule(type))
                        success = true;
                    else
                        failure = true;
                }

                if (success && !failure)
                    return true;
            }

            return false;
        }

        public bool UnloadModule(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            lock (_moduleLockObj)
            {
                LinkedListNode<Type> node = _loadedModules.FindLast(type);
                if (node == null)
                {
                    WriteLogM(LogLevel.Error, $"Can't unload module [{type.FullName}] because it is not loaded.");
                    return false;
                }
                
                return UnloadModule(node);
            }
        }

        private bool UnloadModule(LinkedListNode<Type> node)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            if (node.List != _loadedModules)
                return false;

            Type type = node.Value;

            if (_moduleTypeLookup.TryGetValue(type, out ModuleData moduleInfo) == false)
            {
                return false;
            }

            if (UnloadModule(moduleInfo) == false)
            {
                return false; // can't unload, possibly can't unregister one of its interfaces
            }

            _loadedModules.Remove(moduleInfo.ModuleType);
            _moduleTypeLookup.Remove(moduleInfo.ModuleType);

            Assembly assembly = type.Assembly;
            AssemblyLoadContext loadContext = AssemblyLoadContext.GetLoadContext(assembly);
            if (loadContext != AssemblyLoadContext.Default
                && loadContext is ModulePluginLoadContext moduleLoadContext)
            {
                _pluginModuleTypeSet.Remove(type);

                // Unload the moduleLoadContext if it's the last module from that context/assembly
                if (_pluginModuleTypeSet.Any((t) => t.Assembly == assembly) == false)
                {
                    WriteLogM(LogLevel.Info, $"Unloaded last module from assembly [{assembly.FullName}]");

                    // TODO: also need to do some weak reference stuff?
                    //_loadedPluginAssemblies.Remove
                    //moduleLoadContext.Unload();
                }
            }

            return true;
        }

        #endregion

        #region Bulk Operations

        /// <summary>
        /// Attempts to load any modules that are pending to load.
        /// </summary>
        public void LoadAllModules()
        {
            lock (_moduleLockObj)
            {
                int numModulesLeftToLoad;
                int numModulesLoadedDuringLastPass;

                do
                {
                    numModulesLeftToLoad = 0;
                    numModulesLoadedDuringLastPass = 0;

                    // go through all the modules and try to load each
                    foreach (KeyValuePair<Type, ModuleData> kvp in _moduleTypeLookup)
                    {
                        ModuleData moduleInfo = kvp.Value;
                        if (moduleInfo.IsLoaded)
                            continue; // already loaded

                        if (LoadModule(moduleInfo) == true)
                        {
                            // loaded module
                            numModulesLoadedDuringLastPass++;
                        }
                        else
                        {
                            // not able to load yet
                            numModulesLeftToLoad++;
                        }
                    }
                }
                while ((numModulesLeftToLoad > 0) && (numModulesLoadedDuringLastPass > 0));
            }

            // at this point, we loaded everything we could
            // anything left unloaded is missing at least one dependency
        }

        public void UnloadAllModules()
        {
            lock (_moduleLockObj)
            {
                RecursiveUnload(_loadedModules.First);
            }
        }

        private void RecursiveUnload(LinkedListNode<Type> node)
        {
            if (node == null)
                return;

            RecursiveUnload(node.Next);

            if (UnloadModule(node) == false)
            {
                //Console.Error.WriteLine("<ModuleManager> Error unloading ")
            }
        }

        #endregion

        #region Utility

        public void EnumerateModules(EnumerateModulesDelegate enumerationCallback, Arena arena)
        {
            lock (_moduleLockObj)
            {
                foreach (ModuleData moduleInfo in _moduleTypeLookup.Values)
                {
                    if (arena != null && !moduleInfo.AttachedArenas.Contains(arena))
                        continue;

                    enumerationCallback(moduleInfo.ModuleType, moduleInfo.Description);
                }
            }
        }

        /// <summary>
        /// For returning data from <see cref="GetModuleInfo(string)"/> and <see cref="GetModuleInfo(Type)"/>.
        /// </summary>
        public class ModuleInfo
        {
            internal ModuleInfo(
                string moduleTypeName,
                string moduleQualifiedName,
                string assemblyPath,
                bool isPlugin,
                string description,
                IEnumerable<Arena> attachedArenas)
            {
                ModuleTypeName = moduleTypeName;
                ModuleQualifiedName = moduleQualifiedName;
                AssemblyPath = assemblyPath;
                IsPlugin = isPlugin;
                Description = description;
                AttachedArenas = attachedArenas.ToArray();
            }

            public string ModuleTypeName { get; }
            public string ModuleQualifiedName { get; }
            public string AssemblyPath { get; }
            public bool IsPlugin { get; }
            public string Description { get; }
            public Arena[] AttachedArenas { get; }
        }

        // TODO: change this to return a collection of info (and include the assembly / loader context info)
        // if there are multiple of the same type, then knowing the assembly info (including file path) might be helpful to debug
        public ModuleInfo[] GetModuleInfo(string moduleTypeName)
        {
            if (string.IsNullOrWhiteSpace(moduleTypeName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(moduleTypeName));

            Type moduleType = Type.GetType(moduleTypeName);
            if (moduleType != null)
                return new ModuleInfo[] { GetModuleInfo(moduleType) };

            lock (_moduleLockObj)
            {
                IEnumerable<Type> types = GetPluginModuleTypes(moduleTypeName);

                var moduleInfoArray = (
                    from type in types
                    let moduleInfo = GetModuleInfo(type)
                    where moduleInfo != null
                    select moduleInfo
                ).ToArray();

                return moduleInfoArray;
            }
        }

        public ModuleInfo GetModuleInfo(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            lock (_moduleLockObj)
            {
                if (_moduleTypeLookup.TryGetValue(type, out ModuleData moduleInfo) == false)
                    return null;

                Assembly assembly = type.Assembly;
                return new ModuleInfo(
                    type.FullName, 
                    type.AssemblyQualifiedName,
                    assembly.Location, 
                    _pluginModuleTypeSet.Contains(type),
                    moduleInfo.Description, 
                    moduleInfo.AttachedArenas);
            }
        }

        #endregion

        #region Module Load Stages

        public void DoPostLoadStage()
        {
            lock (_moduleLockObj)
            {
                foreach (ModuleData moduleInfo in _moduleTypeLookup.Values)
                {
                    if (moduleInfo.IsLoaded
                        && moduleInfo.Module is IModuleLoaderAware loaderAwareModule)
                    {
                        loaderAwareModule.PostLoad(this);
                    }
                }
            }
        }

        public void DoPreUnloadStage()
        {
            lock (_moduleLockObj)
            {
                foreach (ModuleData moduleInfo in _moduleTypeLookup.Values)
                {
                    if (moduleInfo.IsLoaded
                        && moduleInfo.Module is IModuleLoaderAware loaderAwareModule)
                    {
                        loaderAwareModule.PreUnload(this);
                    }
                }
            }
        }

        #endregion

        #region ModuleData helper class & helper methods

        private class ModuleData
        {
            public ModuleData(IModule module)
            {
                if (module == null)
                    throw new ArgumentNullException(nameof(module));

                ModuleType = module.GetType();

                if (ModuleInfoAttribute.TryGetAttribute(ModuleType, out ModuleInfoAttribute attribute))
                    Description = attribute.Description;

                Module = module;
                IsLoaded = false;

                if (module.InterfaceDependencies == null)
                {
                    InterfaceDependencies = new Dictionary<Type, IComponentInterface>(0);
                }
                else
                {
                    InterfaceDependencies = new Dictionary<Type, IComponentInterface>(module.InterfaceDependencies.Length);
                    foreach (Type interfaceType in module.InterfaceDependencies)
                    {
                        if (interfaceType.IsInterface == true
                            && typeof(IComponentInterface).IsAssignableFrom(interfaceType))
                        {
                            InterfaceDependencies.Add(interfaceType, null);
                        }
                    }
                }
            }

            public Type ModuleType
            {
                get;
            }

            public IModule Module
            {
                get;
            }

            public string Description
            {
                get;
            }

            public bool IsLoaded
            {
                get;
                set;
            }

            public Dictionary<Type, IComponentInterface> InterfaceDependencies
            {
                get;
            }

            public HashSet<Arena> AttachedArenas
            {
                get;
            } = new HashSet<Arena>();
        }

        private bool LoadModule(ModuleData moduleData)
        {
            if (moduleData == null)
                throw new ArgumentNullException(nameof(moduleData));

            if (moduleData.IsLoaded)
                return false;

            // try to get interfaces
            bool isMissingInterface = false;
            Type[] interfaceKeys = moduleData.InterfaceDependencies.Keys.ToArray();

            foreach (Type interfaceKey in interfaceKeys)
            {
                IComponentInterface moduleInterface = GetInterface(interfaceKey);
                if (moduleInterface == null)
                {
                    // unable to get an interface
                    isMissingInterface = true;
                    break;
                }

                moduleData.InterfaceDependencies[interfaceKey] = moduleInterface;
            }

            if (isMissingInterface)
            {
                ReleaseInterfaces(moduleData);
                return false;
            }

            // we got all the interfaces, now we can load the module
            bool success;

            try
            {
                ReadOnlyDictionary<Type, IComponentInterface> dependencies = new ReadOnlyDictionary<Type, IComponentInterface>(moduleData.InterfaceDependencies);
                success = moduleData.Module.Load(this, dependencies);
            }
            catch (Exception ex)
            {
                success = false;
                WriteLogM(LogLevel.Error, $"Error loading module [{moduleData.ModuleType.FullName}]. Exception: {ex}");
            }

            if (!success)
            {
                // module loading failed
                WriteLogM(LogLevel.Error, $"Error loading module [{moduleData.ModuleType.FullName}]");

                // TODO: maybe we should do something more drastic than ignore and retry later?
                ReleaseInterfaces(moduleData);
                return false;
            }

            moduleData.IsLoaded = true;
            _loadedModules.AddLast(moduleData.ModuleType);
            WriteLogM(LogLevel.Info, $"Loaded module [{moduleData.ModuleType.FullName}]");
            return true;
        }

        private bool UnloadModule(ModuleData moduleData)
        {
            if (moduleData == null)
                throw new ArgumentNullException(nameof(moduleData));

            if (moduleData.AttachedArenas.Count > 0)
            {
                var arenas = moduleData.AttachedArenas.ToArray();
                foreach (Arena arena in arenas)
                {
                    DetachModule(moduleData.ModuleType.AssemblyQualifiedName, arena);
                }

                if (moduleData.AttachedArenas.Count > 0)
                {
                    WriteLogM(LogLevel.Error, $"Can't unload module [{moduleData.ModuleType.FullName}] because it failed to detach from at least one arena.");
                    return false;
                }
            }

            if (moduleData.Module.Unload(this) == false)
            {
                WriteLogM(LogLevel.Error, $"Error unloading module [{moduleData.ModuleType.FullName}]");
                return false;
            }

            ReleaseInterfaces(moduleData);

            moduleData.IsLoaded = false;
            WriteLogM(LogLevel.Info, $"Unloaded module [{moduleData.ModuleType.FullName}]");
            return true;
        }

        private void ReleaseInterfaces(ModuleData moduleData)
        {
            if (moduleData == null)
                throw new ArgumentNullException(nameof(moduleData));

            // release the interfaces we were able to get
            Type[] interfaceKeys = moduleData.InterfaceDependencies.Keys.ToArray();

            foreach (Type interfaceKey in interfaceKeys)
            {
                if (moduleData.InterfaceDependencies[interfaceKey] != null)
                {
                    ReleaseInterface(interfaceKey, moduleData.InterfaceDependencies[interfaceKey]);
                    moduleData.InterfaceDependencies[interfaceKey] = null;
                }
            }
        }

        #endregion

        #region Plug-in

        /// <summary>
        /// Gets the known plugin module types that match on <see cref="Type.FullName"/>.
        /// More than one type can match if:
        /// more than one assembly declared the same type in the same namespace OR
        /// an assembly is loaded more than once (e.g. from 2 file locations which may or may not differ in version)
        /// </summary>
        /// <param name="typeName">The <see cref="Type.FullName"/> to find.</param>
        /// <returns></returns>
        private IEnumerable<Type> GetPluginModuleTypes(string typeName)
        {
            return _pluginModuleTypeSet.Where(t => string.Equals(t.FullName, typeName));
        }

        private Type GetTypeFromPluginAssemblyPath(string typeName, string path)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(typeName));

            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Cannot be null or white-space.", nameof(path));

            try
            {
                path = Path.GetFullPath(path);

                if (!_loadedPluginAssemblies.TryGetValue(path, out Assembly assembly))
                {
                    // Assembly not loaded yet, try to load it.
                    ModulePluginLoadContext loadContext = new ModulePluginLoadContext(path);
                    AssemblyName assemblyName = new AssemblyName(Path.GetFileNameWithoutExtension(path));
                    assembly = loadContext.LoadFromAssemblyName(assemblyName);
                    _loadedPluginAssemblies[path] = assembly;

                    WriteLogM(LogLevel.Info, $"Loaded assembly [{assembly.FullName}] from path \"{path}\"");
                }

                Type type = assembly.GetType(typeName);
                return type;
            }
            catch (Exception ex)
            {
                WriteLogM(LogLevel.Error, $"Error loading assembly from path \"{path}\", exception: {ex}");
                return null;
            }
        }

        /// <summary>
        /// The <see cref="AssemblyLoadContext"/> that is used to  load module "plugins".
        /// This class is <see langword="private"/> to the <see cref="ModuleManager"/> 
        /// which fully manages loading each plugin assembly into a separate, isolated context.
        /// </summary>
        private class ModulePluginLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;

            public ModulePluginLoadContext(string moduleAssemblyPath)
                : base(Path.GetFileNameWithoutExtension(moduleAssemblyPath), true)
            {
                _resolver = new AssemblyDependencyResolver(moduleAssemblyPath);
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                string assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
                if (assemblyPath != null)
                {
                    return LoadFromAssemblyPath(assemblyPath);
                }

                return null;
            }

            protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            {
                string libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
                if (libraryPath != null)
                {
                    return LoadUnmanagedDllFromPath(libraryPath);
                }

                return IntPtr.Zero;
            }
        }

        #endregion

        private void WriteLogA(LogLevel level, Arena arena, string format, params object[] args)
        {
            if (level == LogLevel.Error)
                Console.Error.WriteLine($"{(LogCode)level} <{nameof(ModuleManager)}> {{{arena?.Name ?? "(bad arena)"}}} {string.Format(format, args)}");
            else
                Console.WriteLine($"{(LogCode)level} <{nameof(ModuleManager)}> {{{arena?.Name ?? "(bad arena)"}}} {string.Format(format, args)}");
        }

        private void WriteLogM(LogLevel level, string format, params object[] args)
        {
            if (level == LogLevel.Error)
                Console.Error.WriteLine($"{(LogCode)level} <{nameof(ModuleManager)}> {string.Format(format, args)}");
            else
                Console.WriteLine($"{(LogCode)level} <{nameof(ModuleManager)}> {string.Format(format, args)}");
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

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
    /// <inheritdoc/>
    [CoreModuleInfo]
    public sealed class ModuleManager : ComponentBroker, IModuleManager
    {
        /// <summary>
        /// For synchronizing access to all data members.
        /// </summary>
        private readonly object _moduleLockObj = new();

        /// <summary>
        /// Data for all loaded modules.
        /// </summary>
        private readonly Dictionary<Type, ModuleData> _moduleTypeLookup = new(256);

        /// <summary>
        /// Modules that are loaded, in the order that they were loaded.
        /// </summary>
        private readonly LinkedList<Type> _loadedModules = new();

        /// <summary>
        /// Assembly Path --> Assembly
        /// </summary>
        /// TODO: StringComparer based whether the file system is case sensitive? Also, how to handle a mixture of file systems?
        private readonly Dictionary<string, Assembly> _loadedPluginAssemblies = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Types of 'plug-in' modules that are loaded.
        /// Plug-in modules are modules that are in assemblies loaded (and isolated) into their own <see cref="ModulePluginLoadContext"/> as a 'plug-in'.
        /// </summary>
        private readonly HashSet<Type> _pluginModuleTypeSet = new(256);

        /// <summary>
        /// Whether the post-load stage of the startup sequence has been run.
        /// </summary>
        private bool _isPostLoaded = false;

        /// <summary>
        /// Constructs a <see cref="ModuleManager"/>.
        /// </summary>
        public ModuleManager() : base(null)
        {
            RegisterInterface<IModuleManager>(this);
            RegisterInterface<IComponentBroker>(this);
        }

        #region Arena Attach/Detach

        public bool AttachModule(string moduleTypeName, Arena arena)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleTypeName);
            ArgumentNullException.ThrowIfNull(arena);

            Type? type = Type.GetType(moduleTypeName);
            if (type is not null)
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
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(arena);

            if (!IsModule(type))
            {
                WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Type '{type.FullName}', but it is not a module.");
                return false;
            }

            lock (_moduleLockObj)
            {
                if (!_moduleTypeLookup.TryGetValue(type, out ModuleData? moduleData))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' is not registered, it needs to be loaded first.");
                    return false;
                }

                if (!moduleData.IsLoaded)
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' is not loaded.");
                    return false;
                }

                if (moduleData.AttachedArenas.Contains(arena))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' is already attached to the arena.");
                    return false;
                }

                if (moduleData.Module is not IArenaAttachableModule arenaAttachableModule)
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' does not support attaching.");
                    return false;
                }

                if (!arenaAttachableModule.AttachModule(arena))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' failed to attach.");
                    return false;
                }

                moduleData.AttachedArenas.Add(arena);
                return true;
            }
        }

        public bool DetachModule(string moduleTypeName, Arena arena)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleTypeName);
            ArgumentNullException.ThrowIfNull(arena);

            Type? type = Type.GetType(moduleTypeName);
            if (type is not null)
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
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(arena);

            if (!IsModule(type))
            {
                WriteLogA(LogLevel.Error, arena, $"DetachModule failed: Type '{type.FullName}' is not a module.");
                return false;
            }

            lock (_moduleLockObj)
            {
                if (!_moduleTypeLookup.TryGetValue(type, out ModuleData? moduleData))
                {
                    WriteLogA(LogLevel.Error, arena, $"DetachModule failed: Module '{type.FullName}' is not registered.");
                    return false;
                }

                if (!moduleData.AttachedArenas.Contains(arena))
                {
                    WriteLogA(LogLevel.Error, arena, $"DetachModule failed: Module '{type.FullName}' is not attached to the arena.");
                    return false;
                }

                if (moduleData.Module is not IArenaAttachableModule arenaAttachableModule)
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' does not support attaching.");
                    return false;
                }

                if (!arenaAttachableModule.DetachModule(arena))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' failed to detach.");
                    return false;
                }

                moduleData.AttachedArenas.Remove(arena);
                return true;
            }
        }

        public bool DetachAllFromArena(Arena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);

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

        #region Load Module

        public bool LoadModule(string moduleTypeName) => LoadModule(moduleTypeName, null);

        public bool LoadModule(string moduleTypeName, string? path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleTypeName);

            Type? type;
            if (string.IsNullOrWhiteSpace(path))
            {
                type = Type.GetType(moduleTypeName);
            }
            else
            {
                type = GetTypeFromPluginAssemblyPath(moduleTypeName, path);
            }

            if (type is null)
            {
                // Not found.
                WriteLogM(LogLevel.Error, $"Unable to find module '{moduleTypeName}'.");
                return false;
            }

            return LoadModule(type);
        }

        public bool LoadModule<TModule>() where TModule : class, IModule
        {
            Type type = typeof(TModule);
            return LoadModule(type);
        }

        public bool LoadModule(Type moduleType)
        {
            ArgumentNullException.ThrowIfNull(moduleType);

            if (!IsModule(moduleType))
            {
                // Not a module.
                return false;
            }

            ModuleData? moduleData;

            lock (_moduleLockObj)
            {
                if (_moduleTypeLookup.ContainsKey(moduleType))
                {
                    // Already loaded.
                    return false;
                }

                moduleData = CreateInstance(moduleType);
                if (moduleData is null)
                {
                    // Unable to construct.
                    return false;
                }
            }

            return LoadModule(moduleData);
        }

        public bool LoadModule(IModule module)
        {
            ArgumentNullException.ThrowIfNull(module);

            return LoadModule(module.GetType(), module);
        }

        public bool LoadModule<TModule>(TModule module) where TModule : class, IModule
        {
            ArgumentNullException.ThrowIfNull(module);

            return LoadModule(typeof(TModule), module);
        }

        private bool LoadModule(Type moduleType, IModule module)
        {
            ArgumentNullException.ThrowIfNull(moduleType);
            ArgumentNullException.ThrowIfNull(module);

            lock (_moduleLockObj)
            {
                if (_moduleTypeLookup.ContainsKey(moduleType))
                {
                    // Already loaded.
                    return false;
                }
            }

            ModuleData moduleData = new(module);
            return LoadModule(moduleData);
        }

        #endregion

        #region Load Helper methods

        private static bool IsModule(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);

            // Verify it's a class.
            if (type.IsClass == false)
                return false;

            // Verify it implements the IModule interface.
            if (!typeof(IModule).IsAssignableFrom(type))
                return false;

            return true;
        }

        private ModuleData? CreateInstance(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);

            if (!IsModule(type))
                return null;

            ConstructorInfo[] constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Array.Sort(constructors, (x, y) => -x.GetParameters().Length.CompareTo(y.GetParameters().Length));

            int attempts = 0;

            foreach (ConstructorInfo constructorInfo in constructors)
            {
                ParameterInfo[] parameters = constructorInfo.GetParameters();
                bool isOk = true;

                // Validate the parameters.
                foreach (ParameterInfo parameterInfo in parameters)
                {
                    if (!parameterInfo.ParameterType.IsInterface
                        || !typeof(IComponentInterface).IsAssignableFrom(parameterInfo.ParameterType)
                        || parameterInfo.IsIn
                        || parameterInfo.IsOut
                        || parameterInfo.IsRetval
                        || parameterInfo.IsOptional)
                    {
                        isOk = false;
                    }
                }

                if (!isOk)
                    continue;

                attempts++;

                // Get the dependencies for each parameter.
                DependencyInfo[] dependencies = new DependencyInfo[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    Type parameterType = parameters[i].ParameterType;

                    object? key = null;
                    FromKeyedServicesAttribute? attribute = parameterType.GetCustomAttribute<FromKeyedServicesAttribute>(inherit: false);
                    if (attribute is not null)
                        key = attribute.Key;

                    IComponentInterface? dependency = GetInterface(parameterType, key);
                    if (dependency is null)
                    {
                        isOk = false;
                        break;
                    }

                    dependencies[i] = new DependencyInfo
                    {
                        Type = parameterType,
                        Key = key,
                        Instance = dependency
                    };
                }

                if (!isOk)
                {
                    ReleaseDependencies(dependencies);
                    continue;
                }

                // Create the arguments array to call the constructor with.
                object[] args = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    args[i] = dependencies[i].Instance!;
                }

                IModule module;

                try
                {
                    // Call the constructor.
                    module = (IModule)constructorInfo.Invoke(args);
                }
                catch (Exception ex)
                {
                    WriteLogM(LogLevel.Error, $"Unable to create an instance of '{type.FullName}'. The constructor threw an exception: {ex}");
                    ReleaseDependencies(dependencies);
                    return null;
                }

                return new ModuleData(module, dependencies);
            }

            if (attempts > 0)
            {
                WriteLogM(LogLevel.Error, $"Unable to create an instance of '{type.FullName}'. Found {attempts} constructor{(attempts > 1 ? "s" : "")} but was missing dependencies.");
            }
            else
            {
                WriteLogM(LogLevel.Error, $"Unable to create an instance of '{type.FullName}'. A suitable constructor could not be found.");
            }

            return null;
        }

        #endregion

        #region Unload Module

        public bool UnloadModule(string moduleTypeName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleTypeName);

            Type? type = Type.GetType(moduleTypeName);
            if (type is not null)
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
            ArgumentNullException.ThrowIfNull(type);

            lock (_moduleLockObj)
            {
                LinkedListNode<Type>? node = _loadedModules.FindLast(type);
                if (node is null)
                {
                    WriteLogM(LogLevel.Error, $"Can't unload module [{type.FullName}] because it is not loaded.");
                    return false;
                }

                return UnloadModule(node);
            }
        }

        private bool UnloadModule(LinkedListNode<Type> node)
        {
            ArgumentNullException.ThrowIfNull(node);

            if (node.List != _loadedModules)
                return false;

            Type type = node.Value;

            if (_moduleTypeLookup.TryGetValue(type, out ModuleData? moduleData) == false)
            {
                return false;
            }

            if (UnloadModule(moduleData) == false)
            {
                return false; // can't unload, possibly can't unregister one of its interfaces
            }

            _loadedModules.Remove(node);
            _moduleTypeLookup.Remove(moduleData.ModuleType);

            Assembly assembly = type.Assembly;
            AssemblyLoadContext? loadContext = AssemblyLoadContext.GetLoadContext(assembly);
            if (loadContext != AssemblyLoadContext.Default
                && loadContext is ModulePluginLoadContext moduleLoadContext)
            {
                _pluginModuleTypeSet.Remove(type);

                // Unload the moduleLoadContext if it's the last module from that context/assembly
                if (_pluginModuleTypeSet.Any((t) => t.Assembly == assembly) == false)
                {
                    WriteLogM(LogLevel.Info, $"Unloaded last module from assembly [{assembly.FullName}].");

                    _loadedPluginAssemblies.Remove(moduleLoadContext.AssemblyPath);

                    PluginAssemblyUnloadingCallback.Fire(this, assembly);

                    //moduleLoadContext.Unload(); // TODO: Investigate why this sometimes causes a seg fault on Linux and Mac.
                }
            }

            return true;
        }

        #endregion

        #region Bulk Operations

        public void UnloadAllModules()
        {
            lock (_moduleLockObj)
            {
                LinkedListNode<Type>? node = _loadedModules.Last;
                while (node is not null)
                {
                    LinkedListNode<Type>? previous = node.Previous;
                    UnloadModule(node);
                    node = previous;
                }
            }
        }

        #endregion

        #region Utility

        public void EnumerateModules(EnumerateModulesDelegate enumerationCallback, Arena? arena)
        {
            lock (_moduleLockObj)
            {
                foreach (ModuleData moduleData in _moduleTypeLookup.Values)
                {
                    if (arena != null && !moduleData.AttachedArenas.Contains(arena))
                        continue;

                    enumerationCallback(moduleData.ModuleType, moduleData.Description);
                }
            }
        }

        public IEnumerable<ModuleInfo> GetModuleInfo(string moduleTypeName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleTypeName);

            Type? moduleType = Type.GetType(moduleTypeName);
            if (moduleType is not null && TryGetModuleInfo(moduleType, out ModuleInfo info))
            {
                yield return info;
            }

            lock (_moduleLockObj)
            {
                foreach (Type type in GetPluginModuleTypes(moduleTypeName))
                {
                    if (TryGetModuleInfo(type, out info))
                        yield return info;
                }
            }
        }

        public bool TryGetModuleInfo(Type type, [MaybeNullWhen(false)] out ModuleInfo moduleInfo)
        {
            ArgumentNullException.ThrowIfNull(type);

            lock (_moduleLockObj)
            {
                if (_moduleTypeLookup.TryGetValue(type, out ModuleData? moduleData) == false)
                {
                    moduleInfo = default;
                    return false;
                }

                moduleInfo = new ModuleInfo()
                {
                    Type = type,
                    IsPlugin = _pluginModuleTypeSet.Contains(type),
                    Description = moduleData.Description,
                    AttachedArenas = moduleData.AttachedArenas,
                };
                return true;
            }
        }

        #endregion

        #region Module Load Stages

        /// <summary>
        /// Goes through all loaded modules and has them perform the <see cref="IModuleLoaderAware.PostLoad(ComponentBroker)"/> stage of loading.
        /// </summary>
        public void DoPostLoadStage()
        {
            lock (_moduleLockObj)
            {
                if (!_isPostLoaded)
                {
                    _isPostLoaded = true;

                    LinkedListNode<Type>? node = _loadedModules.First;
                    while (node is not null)
                    {
                        if (_moduleTypeLookup.TryGetValue(node.Value, out ModuleData? moduleData))
                        {
                            PostLoad(moduleData);
                        }

                        node = node.Next;
                    }
                }
            }
        }

        private bool PostLoad(ModuleData moduleData)
        {
            if (moduleData is null)
                return false;

            if (moduleData.IsLoaded
                && !moduleData.IsPostLoaded
                && moduleData.Module is IModuleLoaderAware loaderAwareModule)
            {
                try
                {
                    loaderAwareModule.PostLoad(this);
                    moduleData.IsPostLoaded = true;
                    return true;
                }
                catch (Exception ex)
                {
                    WriteLogM(LogLevel.Warn, $"Error post-loading module [{moduleData.ModuleType.FullName}]. Exception: {ex.Message}");
                }
            }

            return false;
        }

        /// <summary>
        /// Goes through all loaded modules and has them perform the <see cref="IModuleLoaderAware.PreUnload(ComponentBroker)"/> stage of loading.
        /// </summary>
        public void DoPreUnloadStage()
        {
            lock (_moduleLockObj)
            {
                if (_isPostLoaded)
                {
                    _isPostLoaded = false;

                    LinkedListNode<Type>? node = _loadedModules.Last;
                    while (node is not null)
                    {
                        if (_moduleTypeLookup.TryGetValue(node.Value, out ModuleData? moduleData))
                        {
                            PreUnload(moduleData);
                        }

                        node = node.Previous;
                    }
                }
            }
        }

        private bool PreUnload(ModuleData moduleData)
        {
            if (moduleData is null)
                return false;

            if (moduleData.IsLoaded
                && moduleData.IsPostLoaded
                && moduleData.Module is IModuleLoaderAware loaderAwareModule)
            {
                try
                {
                    loaderAwareModule.PreUnload(this);
                    moduleData.IsPostLoaded = false;
                    return true;
                }
                catch (Exception ex)
                {
                    WriteLogM(LogLevel.Warn, $"Error pre-unloading module [{moduleData.ModuleType.FullName}]. Exception: {ex.Message}");
                }
            }

            return false;
        }

        #endregion

        #region ModuleData helper class & helper methods

        private class ModuleData
        {
            public ModuleData(IModule module) : this(module, null)
            {
            }

            public ModuleData(IModule module, DependencyInfo[]? dependencies)
            {
                ArgumentNullException.ThrowIfNull(module);

                ModuleType = module.GetType();

                if (ModuleInfoAttribute.TryGetAttribute(ModuleType, out ModuleInfoAttribute? attribute))
                    Description = attribute.Description;

                Module = module;
                IsLoaded = false;
                Dependencies = dependencies;
            }

            /// <summary>
            /// The <see cref="System.Type"/> of the module.
            /// </summary>
            public Type ModuleType
            {
                get;
            }

            /// <summary>
            /// The instance of the module.
            /// </summary>
            public IModule Module
            {
                get;
            }

            /// <summary>
            /// A description of the module, retrieved from <see cref="ModuleInfoAttribute.Description"/>.
            /// </summary>
            public string? Description
            {
                get;
            }

            /// <summary>
            /// Whether the module has been loaded.
            /// </summary>
            public bool IsLoaded
            {
                get;
                set;
            }

            /// <summary>
            /// Whether the module has been post-loaded.
            /// </summary>
            /// <remarks>
            /// This becomes <see langword="true"/> after <see cref="IModuleLoaderAware.PostLoad(ComponentBroker)"/> has been successfully called,
            /// and goes back to <see langword="false"/> after <see cref="IModuleLoaderAware.PreUnload(ComponentBroker)(ComponentBroker)"/> has been successfully called.
            /// </remarks>
            public bool IsPostLoaded
            {
                get;
                set;
            }

            public DependencyInfo[]? Dependencies
            {
                get;
                private init;
            }

            /// <summary>
            /// Arenas the module is attached to.
            /// </summary>
            public HashSet<Arena> AttachedArenas
            {
                get;
            } = [];
        }

        private record class DependencyInfo
        {
            public required Type Type { get; init; }
            public required object? Key { get; init; }
            public required IComponentInterface? Instance { get; set; }
        }

        private bool LoadModule(ModuleData moduleData)
        {
            ArgumentNullException.ThrowIfNull(moduleData);

            if (moduleData.IsLoaded)
            {
                // Already loaded.
                return false;
            }

            if (_moduleTypeLookup.ContainsKey(moduleData.ModuleType))
            {
                // Another instance already loaded.
                return false;
            }

            bool success;

            try
            {
                success = moduleData.Module.Load(this);

                if (!success)
                {
                    WriteLogM(LogLevel.Error, $"Error loading module [{moduleData.ModuleType.FullName}].");
                }
            }
            catch (Exception ex)
            {
                success = false;
                WriteLogM(LogLevel.Error, $"Error loading module [{moduleData.ModuleType.FullName}]. Exception: {ex}");
            }

            if (!success)
            {
                // module loading failed
                ReleaseDependencies(moduleData);
                return false;
            }

            moduleData.IsLoaded = true;

            _moduleTypeLookup.Add(moduleData.ModuleType, moduleData);
            _loadedModules.AddLast(moduleData.ModuleType);

            Assembly assembly = moduleData.ModuleType.Assembly;
            AssemblyLoadContext? loadContext = AssemblyLoadContext.GetLoadContext(assembly);
            if (loadContext != AssemblyLoadContext.Default
                && loadContext is ModulePluginLoadContext)
            {
                _pluginModuleTypeSet.Add(moduleData.ModuleType);
            }

            WriteLogM(LogLevel.Info, $"Loaded module [{moduleData.ModuleType.FullName}].");

            if (_isPostLoaded)
            {
                // The startup sequence post load stage has already run.
                // After that, any module that gets loaded should also immediately get post loaded too.
                PostLoad(moduleData);
            }

            return true;
        }

        private bool UnloadModule(ModuleData moduleData)
        {
            ArgumentNullException.ThrowIfNull(moduleData);

            if (!moduleData.IsLoaded)
                return true; // it's not loaded, nothing to do

            if (moduleData.AttachedArenas.Count > 0)
            {
                var arenas = moduleData.AttachedArenas.ToArray();
                foreach (Arena arena in arenas)
                {
                    DetachModule(moduleData.ModuleType, arena);
                }

                if (moduleData.AttachedArenas.Count > 0)
                {
                    WriteLogM(LogLevel.Error, $"Can't unload module [{moduleData.ModuleType.FullName}] because it failed to detach from at least one arena.");
                    return false;
                }
            }

            if (moduleData.IsPostLoaded && !PreUnload(moduleData))
            {
                WriteLogM(LogLevel.Error, $"Can't unload module [{moduleData.ModuleType.FullName}] because it failed to pre-unload.");
                return false;
            }

            try
            {
                if (moduleData.Module.Unload(this) == false)
                {
                    WriteLogM(LogLevel.Error, $"Error unloading module [{moduleData.ModuleType.FullName}].");
                    return false;
                }
            }
            catch (Exception ex)
            {
                WriteLogM(LogLevel.Error, $"Error unloading module [{moduleData.ModuleType.FullName}]. Exception: {ex.Message}");
                return false;
            }

            if (moduleData.Module is IDisposable disposable)
            {
                disposable.Dispose();
            }

            ReleaseDependencies(moduleData);

            moduleData.IsLoaded = false;
            WriteLogM(LogLevel.Info, $"Unloaded module [{moduleData.ModuleType.FullName}].");
            return true;
        }

        private void ReleaseDependencies(ModuleData moduleData)
        {
            ArgumentNullException.ThrowIfNull(moduleData);

            if (moduleData.Dependencies is not null)
            {
                ReleaseDependencies(moduleData.Dependencies);
            }
        }

        private void ReleaseDependencies(DependencyInfo[] dependencies)
        {
            ArgumentNullException.ThrowIfNull(dependencies);

            for (int i = 0; i < dependencies.Length; i++)
            {
                DependencyInfo dependency = dependencies[i];
                if (dependency is not null && dependency.Instance is not null)
                {
                    ReleaseInterface(
                        dependency.Type,
                        dependency.Instance,
                        dependency.Key);

                    dependency.Instance = null;
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
            return _pluginModuleTypeSet.Where(t => string.Equals(t.FullName, typeName, StringComparison.Ordinal));
        }

        private Type? GetTypeFromPluginAssemblyPath(string typeName, string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            try
            {
                path = Path.GetFullPath(path);

                Assembly? assembly;
                Type? type;

                lock (_moduleLockObj)
                {
                    if (_loadedPluginAssemblies.TryGetValue(path, out assembly))
                    {
                        return assembly.GetType(typeName);
                    }

                    // Assembly not loaded yet, try to load it.
                    ModulePluginLoadContext loadContext = new(path);
                    AssemblyName assemblyName = new(Path.GetFileNameWithoutExtension(path));
                    assembly = loadContext.LoadFromAssemblyName(assemblyName);

                    type = assembly.GetType(typeName);
                    if (type is null)
                    {
                        loadContext.Unload();
                        return null;
                    }

                    _loadedPluginAssemblies[path] = assembly;
                }

                WriteLogM(LogLevel.Info, $"Loaded assembly [{assembly.FullName}] from path \"{path}\".");

                PluginAssemblyLoadedCallback.Fire(this, assembly);

                return type;
            }
            catch (Exception ex)
            {
                WriteLogM(LogLevel.Error, $"Error loading assembly from path \"{path}\". Exception: {ex}");
                return null;
            }
        }

        /// <summary>
        /// The <see cref="AssemblyLoadContext"/> that is used to load module "plugins".
        /// This class is <see langword="private"/> to the <see cref="ModuleManager"/> 
        /// which fully manages loading each plugin assembly into a separate, isolated context.
        /// </summary>
        private class ModulePluginLoadContext(string moduleAssemblyPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(moduleAssemblyPath), true)
        {
            private readonly AssemblyDependencyResolver _resolver = new(moduleAssemblyPath);

            public string AssemblyPath { get; } = moduleAssemblyPath;

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
                if (assemblyPath is not null)
                {
                    return LoadFromAssemblyPath(assemblyPath);
                }

                return null;
            }

            protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            {
                string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
                if (libraryPath is not null)
                {
                    return LoadUnmanagedDllFromPath(libraryPath);
                }

                return IntPtr.Zero;
            }
        }

        #endregion

        #region Log Methods

        private static void WriteLogA(LogLevel level, Arena arena, string message)
        {
            if (level == LogLevel.Error)
                Console.Error.WriteLine($"{(LogCode)level} <{nameof(ModuleManager)}> {{{arena?.Name ?? "(bad arena)"}}} {message}");
            else
                Console.WriteLine($"{(LogCode)level} <{nameof(ModuleManager)}> {{{arena?.Name ?? "(bad arena)"}}} {message}");
        }

        private static void WriteLogM(LogLevel level, string message)
        {
            if (level == LogLevel.Error)
                Console.Error.WriteLine($"{(LogCode)level} <{nameof(ModuleManager)}> {message}");
            else
                Console.WriteLine($"{(LogCode)level} <{nameof(ModuleManager)}> {message}");
        }

        #endregion
    }
}

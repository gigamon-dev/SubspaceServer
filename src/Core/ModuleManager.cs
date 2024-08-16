using Microsoft.Extensions.DependencyInjection;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

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
        private readonly object _moduleLock = new();

        /// <summary>
        /// Semaphore for allowing a single writer.
        /// </summary>
        /// <remarks>
        /// This is a semaphore since the methods for writing are async.
        /// <para>
        /// When writing to module data, the <see cref="_moduleLock"/> still needs to be held, to prevent concurrent reading.
        /// However, as long as the semaphore is held, it's guaranteed that there are no other writers.
        /// This means that as long as the semaphore is held, even if the <see cref="_moduleLock"/> is released, and then reaquired, 
        /// the data is guaranteed to not have been modified.
        /// </para>
        /// <para>
        /// To prevent deadlock, this semaphore should be taken before the <see cref="_moduleLock"/>.
        /// </para>
        /// </remarks>
        private readonly SemaphoreSlim _moduleSemaphore = new(1, 1);

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

        public async Task<bool> AttachModuleAsync(string moduleTypeName, Arena arena)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleTypeName);
            ArgumentNullException.ThrowIfNull(arena);

            await _moduleSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                Type? type = Type.GetType(moduleTypeName);
                if (type is not null)
                {
                    return await ProcessAttachModule(type, arena).ConfigureAwait(false);
                }

                Type[] types;

                lock (_moduleLock)
                {
                    types = GetPluginModuleTypes(moduleTypeName).ToArray();
                }

                bool success = false;
                bool failure = false;

                foreach (Type t in types)
                {
                    if (await ProcessAttachModule(t, arena).ConfigureAwait(false))
                        success = true;
                    else
                        failure = true;
                }

                if (success && !failure)
                    return true;
            }
            finally
            {
                _moduleSemaphore.Release();
            }

            WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Unable to find module '{moduleTypeName}'.");
            return false;
        }

        public async Task<bool> AttachModuleAsync(Type type, Arena arena)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(arena);

            await _moduleSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                return await ProcessAttachModule(type, arena).ConfigureAwait(false);
            }
            finally
            {
                _moduleSemaphore.Release();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <remarks>
        /// This method assumes the <see cref="_moduleSemaphore"/> is already taken.
        /// </remarks>
        /// <param name="type"></param>
        /// <param name="arena"></param>
        /// <returns></returns>
        private async Task<bool> ProcessAttachModule(Type type, Arena arena)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(arena);

            if (!IsModule(type))
            {
                WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Type '{type.FullName}', but it is not a module.");
                return false;
            }

            ModuleData? moduleData;

            lock (_moduleLock)
            {
                if (!_moduleTypeLookup.TryGetValue(type, out moduleData))
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
            }

            if (moduleData.Module is IAsyncArenaAttachableModule asyncArenaAttachableModule)
            {
                if (!await asyncArenaAttachableModule.AttachModuleAsync(arena, CancellationToken.None).ConfigureAwait(false))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' failed to attach.");
                    return false;
                }
            }
            else if (moduleData.Module is IArenaAttachableModule arenaAttachableModule)
            {
                if (!arenaAttachableModule.AttachModule(arena))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' failed to attach.");
                    return false;
                }
            }
            else
            {
                WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' does not support attaching.");
                return false;
            }

            lock (_moduleLock)
            {
                moduleData.AttachedArenas.Add(arena);
            }

            return true;
        }

        public async Task<bool> DetachModuleAsync(string moduleTypeName, Arena arena)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleTypeName);
            ArgumentNullException.ThrowIfNull(arena);

            await _moduleSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                Type? type = Type.GetType(moduleTypeName);
                if (type is not null)
                {
                    return await ProcessDetachModule(type, arena).ConfigureAwait(false);
                }

                Type[] types;

                lock (_moduleLock)
                {
                    types = GetPluginModuleTypes(moduleTypeName).ToArray();
                }

                bool success = false;
                bool failure = false;

                foreach (Type t in types)
                {
                    if (await ProcessDetachModule(t, arena).ConfigureAwait(false))
                        success = true;
                    else
                        failure = true;
                }

                if (success && !failure)
                    return true;
            }
            finally
            {
                _moduleSemaphore.Release();
            }

            WriteLogA(LogLevel.Error, arena, $"DetachModule failed: Unable to find module '{moduleTypeName}'.");
            return false;
        }

        public async Task<bool> DetachModuleAsync(Type type, Arena arena)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(arena);

            await _moduleSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                return await ProcessDetachModule(type, arena).ConfigureAwait(false);
            }
            finally
            {
                _moduleSemaphore.Release();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <remarks>
        /// This method assumes the <see cref="_moduleSemaphore"/> is already taken.
        /// </remarks>
        /// <param name="type"></param>
        /// <param name="arena"></param>
        /// <returns></returns>
        private async Task<bool> ProcessDetachModule(Type type, Arena arena)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(arena);

            if (!IsModule(type))
            {
                WriteLogA(LogLevel.Error, arena, $"DetachModule failed: Type '{type.FullName}' is not a module.");
                return false;
            }

            ModuleData? moduleData;
            lock (_moduleLock)
            {
                if (!_moduleTypeLookup.TryGetValue(type, out moduleData))
                {
                    WriteLogA(LogLevel.Error, arena, $"DetachModule failed: Module '{type.FullName}' is not registered.");
                    return false;
                }

                if (!moduleData.AttachedArenas.Contains(arena))
                {
                    WriteLogA(LogLevel.Error, arena, $"DetachModule failed: Module '{type.FullName}' is not attached to the arena.");
                    return false;
                }
            }

            if (moduleData.Module is IAsyncArenaAttachableModule asyncArenaAttachableModule)
            {
                if (!await asyncArenaAttachableModule.DetachModuleAsync(arena, CancellationToken.None).ConfigureAwait(false))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' failed to detach.");
                    return false;
                }
            }
            else if (moduleData.Module is IArenaAttachableModule arenaAttachableModule)
            {
                if (!arenaAttachableModule.DetachModule(arena))
                {
                    WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' failed to detach.");
                    return false;
                }
            }
            else
            {
                WriteLogA(LogLevel.Error, arena, $"AttachModule failed: Module '{type.FullName}' does not support attaching.");
                return false;
            }

            lock (_moduleLock)
            {
                moduleData.AttachedArenas.Remove(arena);
            }

            return true;
        }

        public async Task<bool> DetachAllFromArenaAsync(Arena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);

            bool ret = true;

            await _moduleSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                Type[] types;
                lock (_moduleLock)
                {
                    types = new Type[_moduleTypeLookup.Count];
                    int index = 0;
                    foreach (var moduleInfo in _moduleTypeLookup.Values)
                    {
                        if (moduleInfo.AttachedArenas.Contains(arena))
                        {
                            types[index++] = moduleInfo.ModuleType;
                        }
                    }
                }

                foreach (Type? type in types)
                {
                    if (type is null)
                        break;

                    if (!await ProcessDetachModule(type, arena).ConfigureAwait(false))
                        ret = false;
                }
            }
            finally
            {
                _moduleSemaphore.Release();
            }

            return ret;
        }

        #endregion

        #region Load Module

        public async Task<bool> LoadModuleAsync(string moduleTypeName) => await LoadModuleAsync(moduleTypeName, null).ConfigureAwait(false);

        public async Task<bool> LoadModuleAsync(string moduleTypeName, string? path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleTypeName);

            Type? type;
            if (string.IsNullOrWhiteSpace(path))
            {
                type = Type.GetType(moduleTypeName);

                if (type is null)
                {
                    // Not found.
                    WriteLogM(LogLevel.Error, $"Unable to find module \"{moduleTypeName}\".");
                    return false;
                }
            }
            else
            {
                type = GetTypeFromPluginAssemblyPath(moduleTypeName, path);

                if (type is null)
                {
                    // Not found.
                    WriteLogM(LogLevel.Error, $"Unable to find module \"{moduleTypeName}\" from plug-in assembly path \"{path}\".");
                    return false;
                }
            }

            return await LoadModule(type, null).ConfigureAwait(false);
        }

        public async Task<bool> LoadModuleAsync<TModule>() where TModule : class
        {
            Type type = typeof(TModule);
            return await LoadModule(type, null).ConfigureAwait(false);
        }

        public async Task<bool> LoadModuleAsync(Type moduleType)
        {
            ArgumentNullException.ThrowIfNull(moduleType);

            return await LoadModule(moduleType, null).ConfigureAwait(false);
        }

        public async Task<bool> LoadModuleAsync(IModule module)
        {
            ArgumentNullException.ThrowIfNull(module);

            return await LoadModule(module.GetType(), module).ConfigureAwait(false);
        }

        public async Task<bool> LoadModuleAsync(IAsyncModule module)
        {
            ArgumentNullException.ThrowIfNull(module);

            return await LoadModule(module.GetType(), module).ConfigureAwait(false);
        }

        public async Task<bool> LoadModuleAsync<TModule>(TModule module) where TModule : class
        {
            ArgumentNullException.ThrowIfNull(module);

            return await LoadModule(typeof(TModule), module).ConfigureAwait(false);
        }

        private async Task<bool> LoadModule(Type moduleType, object? instance)
        {
            ArgumentNullException.ThrowIfNull(moduleType);

            Debug.Assert(instance is null || instance.GetType() == moduleType);

            if (!IsModule(moduleType))
                return false;

            await _moduleSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                lock (_moduleLock)
                {
                    if (_moduleTypeLookup.ContainsKey(moduleType))
                    {
                        // Already loaded.
                        return false;
                    }
                }

                ModuleData? moduleData;
                if (instance is null)
                {
                    moduleData = CreateInstance(moduleType);
                    if (moduleData is null)
                    {
                        // Unable to construct.
                        return false;
                    }
                }
                else
                {
                    moduleData = new(instance);
                }

                bool success;

                try
                {
                    if (moduleData.Module is IAsyncModule asyncModule)
                    {
                        success = await asyncModule.LoadAsync(this, CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (moduleData.Module is IModule module)
                    {
                        success = module.Load(this);
                    }
                    else
                    {
                        success = false;
                    }

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

                Assembly assembly = moduleData.ModuleType.Assembly;
                AssemblyLoadContext? loadContext = AssemblyLoadContext.GetLoadContext(assembly);
                ModulePluginLoadContext? pluginLoadContext = loadContext as ModulePluginLoadContext;

                if (!success)
                {
                    // module loading failed
                    ReleaseDependencies(moduleData);

                    if (pluginLoadContext is not null)
                    {
                        // Unload the plug-in AssemblyLoadContext if there are no other modules.
                        bool unload = true;
                        foreach (Type pluginType in _pluginModuleTypeSet)
                        {
                            if (AssemblyLoadContext.GetLoadContext(pluginType.Assembly) == pluginLoadContext)
                            {
                                unload = false;
                                break;
                            }
                        }

                        if (unload)
                        {
                            pluginLoadContext.Unload();
                        }
                    }

                    return false;
                }

                moduleData.IsLoaded = true;

                bool isPostLoaded;

                lock (_moduleLock)
                {
                    _moduleTypeLookup.Add(moduleData.ModuleType, moduleData);
                    _loadedModules.AddLast(moduleData.ModuleType);

                    if (pluginLoadContext is not null)
                        _pluginModuleTypeSet.Add(moduleData.ModuleType);

                    isPostLoaded = _isPostLoaded;
                }

                WriteLogM(LogLevel.Info, $"Loaded module [{moduleData.ModuleType.FullName}].");

                if (isPostLoaded)
                {
                    // The startup sequence post load stage has already run.
                    // After that, any module that gets loaded should also immediately get post loaded too.
                    await PostLoad(moduleData).ConfigureAwait(false);
                }

                return true;
            }
            finally
            {
                _moduleSemaphore.Release();
            }
        }

        #endregion

        #region Load Helper methods

        private static bool IsModule(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);

            // Verify it's a class.
            if (type.IsClass == false)
                return false;

            // Verify it implements either the IModule or IAsyncModule interface.
            if (!typeof(IModule).IsAssignableFrom(type) && !typeof(IAsyncModule).IsAssignableFrom(type))
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

                object module;

                try
                {
                    // Call the constructor.
                    module = constructorInfo.Invoke(args);
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

        public async Task<int> UnloadModuleAsync(string moduleTypeName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleTypeName);

            Type? type = Type.GetType(moduleTypeName);
            if (type is not null)
            {
                bool success = await UnloadModuleAsync(type).ConfigureAwait(false);
                return success ? 1 : 0;
            }

            return await UnloadPluginModule(moduleTypeName).ConfigureAwait(false);
        }

        private async Task<int> UnloadPluginModule(string moduleTypeName)
        {
            Type[] types;

            lock (_moduleLock)
            {
                types = GetPluginModuleTypes(moduleTypeName).ToArray();
            }

            int count = 0;

            foreach (Type type in types)
            {
                if (await UnloadModuleAsync(type).ConfigureAwait(false))
                    count++;
            }

            return count;
        }

        public async Task<bool> UnloadModuleAsync(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);

            await _moduleSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                return await ProcessUnloadModule(type).ConfigureAwait(false);
            }
            finally
            {
                _moduleSemaphore.Release();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <remarks>
        /// This method assumes the <see cref="_moduleSemaphore"/> was already entered.
        /// </remarks>
        /// <param name="type"></param>
        /// <returns></returns>
        private async Task<bool> ProcessUnloadModule(Type type)
        {
            LinkedListNode<Type>? node;
            ModuleData? moduleData;
            
            lock (_moduleLock)
            {
                node = _loadedModules.FindLast(type);

                if (node is null)
                {
                    WriteLogM(LogLevel.Error, $"Can't unload module [{type.FullName}] because it is not loaded.");
                    return false;
                }

                if (!_moduleTypeLookup.TryGetValue(type, out moduleData))
                {
                    return false;
                }
            }

            // Detach from arenas
            if (moduleData.AttachedArenas.Count > 0)
            {
                IObjectPoolManager? objectPoolManager = GetInterface<IObjectPoolManager>();

                try
                {
                    HashSet<Arena> arenas = objectPoolManager?.ArenaSetPool.Get() ?? new(moduleData.AttachedArenas.Count);

                    try
                    {
                        // Copy the arenas because can't enumerate the collection and remove from it at the same time.
                        arenas.UnionWith(moduleData.AttachedArenas);

                        foreach (Arena arena in arenas)
                        {
                            await ProcessDetachModule(moduleData.ModuleType, arena).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        objectPoolManager?.ArenaSetPool.Return(arenas);
                    }
                }
                finally
                {
                    if (objectPoolManager is not null)
                        ReleaseInterface(ref objectPoolManager);
                }

                if (moduleData.AttachedArenas.Count > 0)
                {
                    WriteLogM(LogLevel.Error, $"Can't unload module [{moduleData.ModuleType.FullName}] because it failed to detach from at least one arena.");
                    return false;
                }
            }

            // PreUnload
            if (moduleData.IsPostLoaded && !await PreUnload(moduleData).ConfigureAwait(false))
            {
                WriteLogM(LogLevel.Error, $"Can't unload module [{moduleData.ModuleType.FullName}] because it failed to pre-unload.");
                return false;
            }

            // Unload
            bool success;

            try
            {
                if (moduleData.Module is IAsyncModule asyncModule)
                {
                    success = await asyncModule.UnloadAsync(this, CancellationToken.None).ConfigureAwait(false);
                }
                else if (moduleData.Module is IModule module)
                {
                    success = module.Unload(this);
                }
                else
                {
                    success = false;
                }

                if (!success)
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

            // Dispose
            if (moduleData.Module is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (moduleData.Module is IDisposable disposable)
            {
                disposable.Dispose();
            }

            ReleaseDependencies(moduleData);
            
            lock (_moduleLock)
            {
                moduleData.IsLoaded = false;
                _loadedModules.Remove(node);
                _moduleTypeLookup.Remove(moduleData.ModuleType);

                Assembly assembly = type.Assembly;
                AssemblyLoadContext? loadContext = AssemblyLoadContext.GetLoadContext(assembly);
                ModulePluginLoadContext? moduleLoadContext = loadContext as ModulePluginLoadContext;

                if (moduleLoadContext is not null)
                {
                    _pluginModuleTypeSet.Remove(type);
                }

                WriteLogM(LogLevel.Info, $"Unloaded module [{moduleData.ModuleType.FullName}].");

                if (moduleLoadContext is not null)
                {
                    // Unload the moduleLoadContext if it's the last module from that context/assembly
                    bool isLast = true;
                    foreach (Type remainingType in _pluginModuleTypeSet)
                    {
                        if (remainingType.Assembly == assembly)
                        {
                            isLast = false;
                            break;
                        }
                    }

                    if (isLast)
                    {
                        WriteLogM(LogLevel.Info, $"Unloaded the last module from plug-in assembly [{assembly.FullName}].");

                        _loadedPluginAssemblies.Remove(moduleLoadContext.AssemblyPath);

                        PluginAssemblyUnloadingCallback.Fire(this, assembly);

                        // TODO: Investigate why this sometimes causes a seg fault on Linux and Mac.
                        //moduleLoadContext.Unload();
                    }
                }
            }

            return true;
        }

        #endregion

        #region Bulk Operations

        public async Task UnloadAllModulesAsync()
        {
            await _moduleSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                Type[] toUnload;

                lock (_moduleLock)
                {
                    toUnload = new Type[_loadedModules.Count];

                    // Unloading is processed in reverse order.
                    LinkedListNode<Type>? node = _loadedModules.Last;
                    int index = 0;
                    while (node is not null && index < toUnload.Length)
                    {
                        toUnload[index++] = node.Value;
                        node = node.Previous;
                    }
                }

                foreach (Type? type in toUnload)
                {
                    if (type is null)
                        return;

                    await ProcessUnloadModule(type).ConfigureAwait(false);
                }
            }
            finally
            {
                _moduleSemaphore.Release();
            }
        }

        #endregion

        #region Utility

        public IEnumerable<(Type Type, string? Description)> GetModuleTypesAndDescriptions(Arena? arena)
        {
            lock (_moduleLock)
            {
                foreach (ModuleData moduleData in _moduleTypeLookup.Values)
                {
                    if (arena is not null && !moduleData.AttachedArenas.Contains(arena))
                        continue;

                    yield return (moduleData.ModuleType, moduleData.Description);
                }
            }
        }

        public IEnumerable<ModuleInfo> GetModuleInfo(string moduleTypeName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleTypeName);

            Type? moduleType = Type.GetType(moduleTypeName);
            if (moduleType is not null && TryGetModuleInfo(moduleType, out ModuleInfo info))
            {
                // Matched a built-in module.
                yield return info;
            }
            else
            {
                // Search plug-in modules.
                lock (_moduleLock)
                {
                    foreach (Type type in GetPluginModuleTypes(moduleTypeName))
                    {
                        if (TryGetModuleInfo(type, out info))
                            yield return info;
                    }
                }
            }
        }

        public bool TryGetModuleInfo(Type type, [MaybeNullWhen(false)] out ModuleInfo moduleInfo)
        {
            ArgumentNullException.ThrowIfNull(type);

            lock (_moduleLock)
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
        public async Task DoPostLoadStage()
        {
            await _moduleSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                ModuleData[] moduleTypes;
                

                lock (_moduleLock)
                {
                    if (_isPostLoaded)
                        return;

                    _isPostLoaded = true;
                    moduleTypes = new ModuleData[_loadedModules.Count];

                    LinkedListNode<Type>? node = _loadedModules.First;
                    int index = 0;
                    while (node is not null && index < moduleTypes.Length)
                    {
                        if (_moduleTypeLookup.TryGetValue(node.Value, out ModuleData? moduleData))
                        {
                            moduleTypes[index++] = moduleData;
                        }

                        node = node.Next;
                    }
                }

                foreach (ModuleData? moduleData in moduleTypes)
                {
                    if (moduleData is null)
                        break;

                    await PostLoad(moduleData).ConfigureAwait(false);
                }
            }
            finally
            {
                _moduleSemaphore.Release();
            }
        }

        private async Task<bool> PostLoad(ModuleData moduleData)
        {
            if (moduleData is null)
                return false;

            if (moduleData.IsLoaded && !moduleData.IsPostLoaded)
            {
                try
                {
                    if (moduleData.Module is IAsyncModuleLoaderAware asyncloaderAwareModule)
                    {
                        await asyncloaderAwareModule.PostLoadAsync(this, CancellationToken.None).ConfigureAwait(false);
                        moduleData.IsPostLoaded = true;
                        return true;
                    }
                    else if (moduleData.Module is IModuleLoaderAware loaderAwareModule)
                    {
                        loaderAwareModule.PostLoad(this);
                        moduleData.IsPostLoaded = true;
                        return true;
                    }
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
        public async Task DoPreUnloadStage()
        {
            await _moduleSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                ModuleData[] moduleTypes;


                lock (_moduleLock)
                {
                    if (!_isPostLoaded)
                        return;

                    _isPostLoaded = false;
                    moduleTypes = new ModuleData[_loadedModules.Count];

                    // Process in reverse order.
                    LinkedListNode<Type>? node = _loadedModules.Last;
                    int index = 0;
                    while (node is not null && index < moduleTypes.Length)
                    {
                        if (_moduleTypeLookup.TryGetValue(node.Value, out ModuleData? moduleData))
                        {
                            moduleTypes[index++] = moduleData;
                        }

                        node = node.Previous;
                    }
                }

                foreach (ModuleData? moduleData in moduleTypes)
                {
                    if (moduleData is null)
                        break;

                    await PreUnload(moduleData).ConfigureAwait(false);
                }
            }
            finally
            {
                _moduleSemaphore.Release();
            }
        }

        private async Task<bool> PreUnload(ModuleData moduleData)
        {
            if (moduleData is null)
                return false;

            if (moduleData.IsLoaded && moduleData.IsPostLoaded)
            {
                try
                {
                    if (moduleData.Module is IAsyncModuleLoaderAware asyncLoaderAwareModule)
                    {
                        await asyncLoaderAwareModule.PreUnloadAsync(this, CancellationToken.None).ConfigureAwait(false);
                        moduleData.IsPostLoaded = false;
                        return true;
                    }
                    else if (moduleData.Module is IModuleLoaderAware loaderAwareModule)
                    {
                        loaderAwareModule.PreUnload(this);
                        moduleData.IsPostLoaded = false;
                        return true;
                    }
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
            public ModuleData(object module) : this(module, null)
            {
            }

            public ModuleData(object module, DependencyInfo[]? dependencies)
            {
                ArgumentNullException.ThrowIfNull(module);

                ModuleType = module.GetType();

                if (!IsModule(ModuleType))
                    throw new ArgumentException("Is not a module.", nameof(module));


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
            public object Module
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
            foreach (Type type in _pluginModuleTypeSet)
            {
                if (string.Equals(type.FullName, typeName, StringComparison.Ordinal))
                    yield return type;
            }
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

                lock (_moduleLock)
                {
                    if (_loadedPluginAssemblies.TryGetValue(path, out assembly))
                    {
                        return assembly.GetType(typeName);
                    }

                    // Assembly not loaded yet, try to load it.
                    ModulePluginLoadContext loadContext = new(path);
                    AssemblyName assemblyName = new(Path.GetFileNameWithoutExtension(path));

                    try
                    {
                        assembly = loadContext.LoadFromAssemblyName(assemblyName);
                    }
                    catch (Exception ex)
                    {
                        WriteLogM(LogLevel.Error, $"Error loading plug-in assembly from path \"{path}\". Exception: {ex}");
                        loadContext.Unload();
                        return null;
                    }

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
                WriteLogM(LogLevel.Error, $"Error getting type \"{typeName}\" from plug-in assembly path \"{path}\". Exception: {ex}");
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

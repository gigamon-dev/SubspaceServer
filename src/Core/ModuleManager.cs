using SS.Core.ComponentInterfaces;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace SS.Core
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public class ModuleInfoAttribute : Attribute
    {
        public ModuleInfoAttribute(string description)
        {
            Description = description;
        }

        public string Description { get; }

        public static bool TryGetAttribute(Type type, out ModuleInfoAttribute attribute)
        {
            if (type == null)
            {
                attribute = null;
                return false;
            }

            attribute = Attribute.GetCustomAttribute(type, typeof(ModuleInfoAttribute)) as ModuleInfoAttribute;
            return attribute != null;
        }
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    internal class CoreModuleInfoAttribute : ModuleInfoAttribute
    {
        public CoreModuleInfoAttribute()
            : base($"Subspace Server .NET ({Assembly.GetExecutingAssembly().GetName().Version})")
        {
        }
    }

    public interface IModule
    {
        /// <summary>
        /// IComponentInterfaces that are required by the Module to load properly.
        /// *** DO NOT INCLUDE OPTIONAL INTERFACES HERE ***
        /// </summary>
        Type[] InterfaceDependencies
        {
            get;
        }

        bool Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies);
        bool Unload(ModuleManager mm);

        // moved these to IModuleArenaAttachable
        //void Attach(Arena arena);
        //void Detach(Arena arena);
    }

    /// <summary>
    /// A "Module" can implement this interface if it needs to do work 
    /// after being loaded (<see cref="PostLoad(ModuleManager)"/>) 
    /// or 
    /// before being unloaded (<see cref="PreUnload(ModuleManager)"/>).
    /// </summary>
    public interface IModuleLoaderAware
    {
        bool PostLoad(ModuleManager mm);
        bool PreUnload(ModuleManager mm);
    }

    /// <summary>
    /// Equivalent of ASSS' module.[ch]
    /// Completely different style though...
    /// 
    /// Modules are loaded based on their dependencies.
    /// If modules have cyclic dependencies, they simply will not be loaded.
    /// 
    /// Note to self: I am thinking that dependencies are actually on the interfaces and callbacks, not directly on other modules?
    /// But with interfaces and callbacks, some are somewhat static (registered on load) and some are somewhat dynamic (registered later on)
    /// 
    /// Another issue I will have to make a decision on is if the ModuleManager should know anything about the Arena class.  That is, 
    /// whether an Arena object contains the methods to register interfaces and callbacks to itself.
    /// </summary>
    [CoreModuleInfo]
    public sealed class ModuleManager : ComponentBroker, IModuleManager
    {
        private class ModuleInfo
        {
            public ModuleInfo(IModule module)
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

        // for managing modules
        private readonly object _moduleLockObj = new object();
        private readonly Dictionary<Type, ModuleInfo> _moduleTypeLookup = new Dictionary<Type, ModuleInfo>();
        private readonly LinkedList<Type> _loadedModules = new LinkedList<Type>();

        public ModuleManager()
        {
            RegisterInterface<IModuleManager>(this);
        }

        public bool AttachModule(string assemblyQualifiedName, Arena arena)
        {
            if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(assemblyQualifiedName));

            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            Type type = Type.GetType(assemblyQualifiedName);
            if (type == null)
            {
                Console.Error.WriteLine($"E <ModuleManager> {{{arena}}} AttachModule failed: Unable to find module '{assemblyQualifiedName}'.");
                return false;
            }

            if (!IsModule(type))
            {
                Console.Error.WriteLine($"E <ModuleManager> {{{arena}}} AttachModule failed: Found type '{assemblyQualifiedName}', but it is not a module .");
                return false;
            }

            lock (_moduleLockObj)
            {
                if (!_moduleTypeLookup.TryGetValue(type, out ModuleInfo moduleInfo))
                {
                    Console.Error.WriteLine($"E <ModuleManager> {{{arena}}} AttachModule failed: Module '{assemblyQualifiedName}' is not registered, it needs to be loaded first.");
                    return false;
                }

                if (!moduleInfo.IsLoaded)
                {
                    Console.Error.WriteLine($"E <ModuleManager> {{{arena}}} AttachModule failed: Module '{assemblyQualifiedName}' is not loaded.");
                    return false;
                }

                if (moduleInfo.AttachedArenas.Contains(arena))
                {
                    Console.Error.WriteLine($"E <ModuleManager> {{{arena}}} AttachModule failed: Module '{assemblyQualifiedName}' is already attached to the arena.");
                    return false;
                }

                if (!(moduleInfo.Module is IArenaAttachableModule arenaAttachableModule))
                {
                    Console.Error.WriteLine($"E <ModuleManager> {{{arena}}} AttachModule failed: Module '{assemblyQualifiedName}' does not support attaching.");
                    return false;
                }

                if (!arenaAttachableModule.AttachModule(arena))
                {
                    Console.Error.WriteLine($"E <ModuleManager> {{{arena}}} AttachModule failed: Module '{assemblyQualifiedName}' failed to attach .");
                    return false;
                }

                moduleInfo.AttachedArenas.Add(arena);
                return true;
            }
        }

        public bool DetachModule(string assemblyQualifiedName, Arena arena)
        {
            if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(assemblyQualifiedName));

            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            Type type = Type.GetType(assemblyQualifiedName);
            if (type == null)
            {
                Console.Error.WriteLine($"E <ModuleManager> {{{arena}}} DetachModule failed: Unable to find module '{assemblyQualifiedName}'.");
                return false;
            }

            if (!IsModule(type))
            {
                Console.Error.WriteLine($"E <ModuleManager> {{{arena}}}  DetachModule failed: Found type '{assemblyQualifiedName}', but it is not a module .");
                return false;
            }

            lock (_moduleLockObj)
            {
                if (!_moduleTypeLookup.TryGetValue(type, out ModuleInfo moduleInfo))
                {
                    Console.Error.WriteLine($"E <ModuleManager> {{{arena}}} DetachModule failed: Module '{assemblyQualifiedName}' is not registered.");
                    return false;
                }

                if (!moduleInfo.AttachedArenas.Contains(arena))
                {
                    Console.Error.WriteLine($"E <ModuleManager> {{{arena}}} DetachModule failed: Module '{assemblyQualifiedName}' is not attached to the arena.");
                    return false;
                }

                if (!(moduleInfo.Module is IArenaAttachableModule arenaAttachableModule))
                {
                    Console.Error.WriteLine($"E <ModuleManager> {{{arena}}} AttachModule failed: Module '{assemblyQualifiedName}' does not support attaching.");
                    return false;
                }

                if (!arenaAttachableModule.DetachModule(arena))
                {
                    Console.Error.WriteLine($"E <ModuleManager> {{{arena}}} AttachModule failed: Module '{assemblyQualifiedName}' failed to detach .");
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
                        if (!DetachModule(moduleInfo.ModuleType.AssemblyQualifiedName, arena))
                            ret = false;
                    }
                }
            }

            return ret;
        }

        /// <summary>
        /// Adds a module to be loaded later.
        /// </summary>
        /// <param name="assemblyQualifiedName">The assembly to load in the format of <see cref="Type.AssemblyQualifiedName"/>.</param>
        /// <returns>True if the module was added. Otherwise, false.</returns>
        public bool AddModule(string assemblyQualifiedName) => AddAndLoadModule(assemblyQualifiedName, false);

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

        /// <summary>
        /// Adds and loads a module.
        /// </summary>
        /// <param name="assemblyQualifiedName">The assembly to load in the format of <see cref="Type.AssemblyQualifiedName"/>.</param>
        /// <returns>True if the module was added and loaded. Otherwise, false.</returns>
        public bool LoadModule(string assemblyQualifiedName) => AddAndLoadModule(assemblyQualifiedName, true);

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

        private bool AddAndLoadModule(string assemblyQualifiedName, bool load)
        {
            if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(assemblyQualifiedName));

            Type type = Type.GetType(assemblyQualifiedName);
            if (type == null)
            {
                Console.Error.WriteLine($"E <ModuleManager> Unable to find module '{assemblyQualifiedName}'.");
                return false;
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

                ModuleInfo moduleInfo = new ModuleInfo(module);
                _moduleTypeLookup.Add(moduleType, moduleInfo);

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

        /// <summary>
        /// Unloads a module.
        /// </summary>
        /// <param name="assemblyQualifiedName">The type to unload in the format of <see cref="Type.AssemblyQualifiedName"/>.</param>
        /// <returns>True if the module was unloaded. Otherwise false.</returns>
        public bool UnloadModule(string assemblyQualifiedName)
        {
            if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(assemblyQualifiedName));

            Type typeToRemove = Type.GetType(assemblyQualifiedName);
            if (typeToRemove == null)
                return false;

            lock (_moduleLockObj)
            {
                if (_moduleTypeLookup.TryGetValue(typeToRemove, out ModuleInfo moduleInfo) == false)
                    return false;

                if (UnloadModule(moduleInfo) == false)
                {
                    // TODO: write an error log
                    return false; // can't unload, possibly can't unregister one of its interfaces
                }

                _loadedModules.Remove(moduleInfo.ModuleType);
                _moduleTypeLookup.Remove(moduleInfo.ModuleType);
            }

            return true;
        }

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
                    foreach (KeyValuePair<Type, ModuleInfo> kvp in _moduleTypeLookup)
                    {
                        ModuleInfo moduleInfo = kvp.Value;
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
                LinkedListNode<Type> node = _loadedModules.Last;
                while (node != null)
                {
                    LinkedListNode<Type> toRemove = null;

                    if (_moduleTypeLookup.TryGetValue(node.Value, out ModuleInfo moduleInfo) == true)
                    {
                        if (UnloadModule(moduleInfo) == false)
                        {
                            // TODO: write error log
                        }
                        else
                        {
                            toRemove = node;
                        }
                    }

                    node = node.Previous;

                    if (toRemove != null)
                    {
                        _moduleTypeLookup.Remove(toRemove.Value);
                        _loadedModules.Remove(toRemove);
                    }
                }
            }
        }

        public void EnumerateModules(EnumerateModulesDelegate enumerationCallback, Arena arena)
        {
            lock (_moduleLockObj)
            {
                foreach (ModuleInfo moduleInfo in _moduleTypeLookup.Values)
                {
                    if (arena != null && !moduleInfo.AttachedArenas.Contains(arena))
                        continue;

                    enumerationCallback(moduleInfo.ModuleType, moduleInfo.Description);
                }
            }
        }

        public string GetModuleInfo(string assemblyQualifiedName)
        {
            if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(assemblyQualifiedName));

            Type moduleType = Type.GetType(assemblyQualifiedName);
            if (moduleType == null)
                return null;

            lock (_moduleLockObj)
            {
                if (_moduleTypeLookup.TryGetValue(moduleType, out ModuleInfo moduleInfo) == false)
                    return null;

                return moduleInfo.Description;
            }
        }

        public void DoPostLoadStage()
        {
            lock (_moduleLockObj)
            {
                foreach (ModuleInfo moduleInfo in _moduleTypeLookup.Values)
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
                foreach (ModuleInfo moduleInfo in _moduleTypeLookup.Values)
                {
                    if (moduleInfo.IsLoaded
                        && moduleInfo.Module is IModuleLoaderAware loaderAwareModule)
                    {
                        loaderAwareModule.PreUnload(this);
                    }
                }
            }
        }

        #region ModuleInfo helper methods

        private bool LoadModule(ModuleInfo moduleInfo)
        {
            if (moduleInfo == null)
                throw new ArgumentNullException(nameof(moduleInfo));

            if (moduleInfo.IsLoaded)
                return false;

            // try to get interfaces
            bool isMissingInterface = false;
            Type[] interfaceKeys = moduleInfo.InterfaceDependencies.Keys.ToArray();

            foreach (Type interfaceKey in interfaceKeys)
            {
                IComponentInterface moduleInterface = GetInterface(interfaceKey);
                if (moduleInterface == null)
                {
                    // unable to get an interface
                    isMissingInterface = true;
                    break;
                }

                moduleInfo.InterfaceDependencies[interfaceKey] = moduleInterface;
            }

            if (isMissingInterface)
            {
                ReleaseInterfaces(moduleInfo);
                return false;
            }

            // we got all the interfaces, now we can load the module
            if (moduleInfo.Module.Load(this, moduleInfo.InterfaceDependencies) == false)
            {
                // module loading failed
                Console.Error.WriteLine(string.Format($"E <ModuleManager> Error loading module [{moduleInfo.ModuleType.FullName}]"));

                // TODO: maybe we should do something more drastic than ignore and retry later?
                ReleaseInterfaces(moduleInfo);
                return false;
            }

            moduleInfo.IsLoaded = true;
            _loadedModules.AddLast(moduleInfo.ModuleType);
            Console.WriteLine(string.Format($"I <ModuleManager> Loaded module [{moduleInfo.ModuleType.FullName}]"));
            return true;
        }

        private bool UnloadModule(ModuleInfo moduleInfo)
        {
            if (moduleInfo == null)
                throw new ArgumentNullException(nameof(moduleInfo));

            if (moduleInfo.AttachedArenas.Count > 0)
            {
                var arenas = moduleInfo.AttachedArenas.ToArray();
                foreach (Arena arena in arenas)
                {
                    DetachModule(moduleInfo.ModuleType.AssemblyQualifiedName, arena);
                }

                if (moduleInfo.AttachedArenas.Count > 0)
                {
                    return false;
                }
            }

            if (moduleInfo.Module.Unload(this) == false)
            {
                Console.Error.WriteLine($"E <ModuleManager> Error unloading module [{moduleInfo.ModuleType.FullName}]");
                return false;
            }

            ReleaseInterfaces(moduleInfo);

            moduleInfo.IsLoaded = false;
            Console.WriteLine(string.Format($"I <ModuleManager> Unloaded module [{moduleInfo.ModuleType.FullName}]"));
            return true;
        }

        private void ReleaseInterfaces(ModuleInfo moduleInfo)
        {
            if (moduleInfo == null)
                throw new ArgumentNullException(nameof(moduleInfo));

            // release the interfaces we were able to get
            Type[] interfaceKeys = moduleInfo.InterfaceDependencies.Keys.ToArray();

            foreach (Type interfaceKey in interfaceKeys)
            {
                if (moduleInfo.InterfaceDependencies[interfaceKey] != null)
                {
                    ReleaseInterface(interfaceKey);
                    moduleInfo.InterfaceDependencies[interfaceKey] = null;
                }
            }
        }

        #endregion
    }
}

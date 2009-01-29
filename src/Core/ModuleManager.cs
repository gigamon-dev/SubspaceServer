using System;
using System.Collections.Generic;
using System.Text;
using System.ComponentModel;

namespace SS.Core
{
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

    public interface IModuleLoaderAware
    {
        bool PostLoad(ModuleManager mm);
        bool PreUnload(ModuleManager mm);
    }

    /* TODO
    public class ModuleLoadedEventArgs : EventArgs
    {
        public readonly IModule Module;

        public ModuleLoadedEventArgs(IModule module)
        {
            Module = module;
        }
    }
    */
    public class ModuleUnloadingEventArgs : CancelEventArgs
    {
        public readonly IModule Module;
        public string CancelReason;

        public ModuleUnloadingEventArgs(IModule module) : base(false)
        {
            Module = module;
        }
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
    /// Another issue I will have to make a decision os is if the ModuleManager should know anything about the Arena class.  That is, 
    /// whether an Arena object contains the methods to register interfaces and callbacks to itself.
    /// </summary>
    public sealed class ModuleManager : ComponentBroker
    {
        private class ModuleInfo
        {
            private readonly IModule _module;
            private bool _isLoaded = false;
            private readonly Dictionary<Type, IComponentInterface> _interfaceDependencies;

            public ModuleInfo(IModule module)
            {
                _module = module;

                if (module.InterfaceDependencies == null)
                {
                    _interfaceDependencies = new Dictionary<Type, IComponentInterface>(0);
                }
                else
                {
                    _interfaceDependencies = new Dictionary<Type, IComponentInterface>(module.InterfaceDependencies.Length);
                    foreach (Type interfaceType in module.InterfaceDependencies)
                    {
                        if (interfaceType.IsInterface == true)
                        {
                            _interfaceDependencies.Add(interfaceType, null);
                        }
                    }
                }
            }

            public bool IsLoaded
            {
                get { return _isLoaded; }
            }

            public IModule Module
            {
                get { return _module; }
            }

            public bool LoadModule(ModuleManager mm)
            {
                if (IsLoaded)
                    return false;

                // try to get interfaces
                bool gotAllInterfaces = true;
                List<Type> interfaceKeyList = new List<Type>(_interfaceDependencies.Count);
                interfaceKeyList.AddRange(_interfaceDependencies.Keys);

                foreach (Type interfaceKey in interfaceKeyList)
                {
                    IComponentInterface moduleInterface = mm.GetInterface(interfaceKey);
                    if (moduleInterface == null)
                    {
                        // unable to get an interface
                        gotAllInterfaces = false;
                        break;
                    }
                    _interfaceDependencies[interfaceKey] = moduleInterface;
                }

                if (!gotAllInterfaces)
                {
                    releaseInterfaces(mm);
                    return false;
                }

                // we got all the interfaces, now we can load the module
                if (_module.Load(mm, _interfaceDependencies) == false)
                {
                    // module loading failed
                    Console.WriteLine(string.Format("[ModuleManager] failed to load module [{0}]", _module.GetType().FullName));

                    // TODO: maybe we should do something more drastic than ignore and retry later?
                    releaseInterfaces(mm);
                    return false;
                }

                _isLoaded = true;
                mm._loadedModules.AddLast(_module.GetType());
                Console.WriteLine(string.Format("[ModuleManager] loaded module [{0}]", _module.GetType().FullName));
                return true;
            }

            public bool UnloadModule(ModuleManager mm)
            {
                if (mm.ModuleUnloading != null)
                {
                    ModuleUnloadingEventArgs args = new ModuleUnloadingEventArgs(_module);
                    mm.ModuleUnloading(this, args);
                    if (args.Cancel == true)
                    {
                        throw new Exception(args.CancelReason);
                    }
                }

                if (_module.Unload(mm) == false)
                {
                    throw new Exception("error unloading module");
                }

                releaseInterfaces(mm);

                _isLoaded = false;
                Console.WriteLine(string.Format("[ModuleManager] unloaded module [{0}]", _module.GetType().FullName));
                return true;
            }

            private void releaseInterfaces(ModuleManager mm)
            {
                // release the interfaces we were able to get
                List<Type> interfaceKeyList = new List<Type>(_interfaceDependencies.Count);
                interfaceKeyList.AddRange(_interfaceDependencies.Keys);

                foreach (Type interfaceKey in interfaceKeyList)
                {
                    if (_interfaceDependencies[interfaceKey] != null)
                    {
                        mm.ReleaseInterface(interfaceKey);
                        _interfaceDependencies[interfaceKey] = null;
                    }
                }
            }
        }

        // all known modules
        private object _moduleLockObj = new object();
        private Dictionary<Type, ModuleInfo> _moduleTypeLookup = new Dictionary<Type, ModuleInfo>();
        private LinkedList<Type> _loadedModules = new LinkedList<Type>();
        

        //TODO: public event EventHandler<ModuleLoadedEventArgs> ModuleLoaded;

        /// <summary>
        /// Occurs before a module is unloaded.
        /// </summary>
        public event EventHandler<ModuleUnloadingEventArgs> ModuleUnloading;

        public ModuleManager()
        {
        }
        
        /// <summary>
        /// Adds a module to be loaded later.
        /// </summary>
        /// <param name="moduleToAdd"></param>
        public void AddModule(IModule moduleToAdd)
        {
            Type moduleType = moduleToAdd.GetType();
            ModuleInfo moduleInfo = new ModuleInfo(moduleToAdd);

            lock (_moduleLockObj)
            {
                if (_moduleTypeLookup.ContainsKey(moduleType) == false)
                {
                    _moduleTypeLookup.Add(moduleType, moduleInfo);
                }
            }
        }

        /// <summary>
        /// Adds a module to be loaded later.
        /// </summary>
        /// <typeparam name="TModule"></typeparam>
        /// <param name="moduleToAdd"></param>
        public void AddModule<TModule>(TModule moduleToAdd) where TModule : class, IModule
        {
            Type moduleType = typeof(TModule); // guessing this might be faster than object.GetType() since part of it is compile time
            ModuleInfo moduleInfo = new ModuleInfo(moduleToAdd);

            lock (_moduleLockObj)
            {
                if (_moduleTypeLookup.ContainsKey(moduleType) == false)
                {
                    _moduleTypeLookup.Add(moduleType, moduleInfo);
                }
            }
        }

        /* if i do supply a method like this, i will probably want to reference count
        public TModule GetModule<TModule>() where TModule : class, IModule
        {
            lock (_lockObj)
            {
                ModuleInfo moduleInfo;
                _moduleTypeLookup.TryGetValue(typeof(TModule), out moduleInfo);
                return moduleInfo.Module as TModule;
            }
        }
        */

        /// <summary>
        /// Loads a module.
        /// </summary>
        /// <param name="moduleToLoad">module to load</param>
        /// <returns>
        /// true - module loaded successfuly
        /// false - module did not load or module already loaded
        /// </returns>
        public bool LoadModule(IModule moduleToLoad)
        {
            Type t = moduleToLoad.GetType();
            ModuleInfo moduleInfo;

            lock (_moduleLockObj)
            {
                if (_moduleTypeLookup.TryGetValue(t, out moduleInfo) == false)
                {
                    // module wasn't previously added, do it now
                    moduleInfo = new ModuleInfo(moduleToLoad);
                    _moduleTypeLookup.Add(t, moduleInfo);
                }

                // TODO: handle exceptions that are thrown when loading
                if(moduleInfo.LoadModule(this) == false)
                {
                    return false;
                }

                return true;
            }
        }
        /*
        private bool loadModule(ModuleInfo moduleInfo)
        {
            lock (_lockObj)
            {
                if (moduleInfo.IsLoaded)
                    return false;

                //IModule module = moduleInfo.Module;
                //if (moduleInfo.TryGetInterfaces(this) == true)
                //{
                    //moduleInfo.LoadModule();
                //}

                List<Type> interfacesNeeded = moduleInfo.InterfacesNeeded;

                // in order to load a module, we must first have all of its dependencies
                // try to get all of the module's depedencies
                for (int x = interfacesNeeded.Count - 1; x >= 0; x--)
                {
                    // try to find the dependency
                    IModuleInterface neededModuleInterface;
                    if (_interfaceLookup.TryGetValue(interfacesNeeded[x], out neededModuleInterface) == true)
                    {
                        // found the dependency
                        moduleInfo.InterfaceDependencies[interfacesNeeded[x]] = neededModuleInterface;
                        interfacesNeeded.RemoveAt(x);
                    }
                }

                if (interfacesNeeded.Count == 0)
                {
                    // module has all of its dependencies, load it
                    System.Console.WriteLine("[ModuleManager] loading module: " + moduleInfo.Module.GetType().ToString());
                    if (moduleInfo.Module.Load(this, moduleInfo.InterfaceDependencies) == true)
                    {
                        moduleInfo.IsLoaded = true;
                        return true;
                    }
                    else
                    {
                        Console.WriteLine("[ModuleManager] failed to load module: " + moduleInfo.Module.GetType().ToString());
                        return false;
                    }
                }

                return false;
            }
        }
*/
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

                        if(moduleInfo.LoadModule(this) == true)
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

        public delegate void EnumerateModulesDelegate(IModule module, bool isLoaded);

        public void EnumerateModules(EnumerateModulesDelegate enumerationCallback)
        {
            lock (_moduleLockObj)
            {
                foreach(ModuleInfo moduleInfo in _moduleTypeLookup.Values)
                {
                    enumerationCallback(moduleInfo.Module, moduleInfo.IsLoaded);
                }
            }
        }

        /// <summary>
        /// Unloads a module.
        /// </summary>
        /// <param name="moduleName">name of the module to unload</param>
        /// <exception cref="System.Exception">error stating why module unloading</exception>
        public void UnloadModule(string moduleName)
        {
            lock (_moduleLockObj)
            {
                foreach (KeyValuePair<Type, ModuleInfo> kvp in _moduleTypeLookup)
                {
                    if (kvp.Key.FullName == moduleName)
                    {
                        if (kvp.Value.UnloadModule(this) == true)
                        {
                            _moduleTypeLookup.Remove(kvp.Key);

                            LinkedListNode<Type> node = _loadedModules.Last;
                            while (node != null)
                            {
                                if (node.Value == kvp.Key)
                                {
                                    _loadedModules.Remove(node);
                                    return;
                                }
                            }
                            return;
                        }
                    }
                }
            }

            // getting to here, means we were unsuccessful in finding the module to unload
            throw new Exception(string.Format("unable to find module by the name of [{0}]", moduleName));
        }

        public void UnloadAllModules()
        {
            lock (_moduleLockObj)
            {
                LinkedListNode<Type> node = _loadedModules.Last;
                while (node != null)
                {
                    ModuleInfo moduleInfo;
                    if (_moduleTypeLookup.TryGetValue(node.Value, out moduleInfo) == true)
                    {
                        moduleInfo.UnloadModule(this);
                    }

                    node = node.Previous;
                    _loadedModules.RemoveLast();
                }
            }
        }
    }
}

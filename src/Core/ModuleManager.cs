using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core
{
    public interface IModule
    {
        Type[] ModuleDependencies
        {
            get;
        }

        bool Load(ModuleManager mm, Dictionary<Type, IModule> moduleDependencies);
        bool Unload();
        //void Attach(Arena arena);
        //void Detach(Arena arena);
    }

    /// <summary>
    /// Equivalent of ASSS' module.[ch]
    /// Completely different style though...
    /// 
    /// Modules are loaded based on their dependencies.
    /// If modules have cyclic dependencies, they simply will not be loaded.
    /// </summary>
    public class ModuleManager
    {
        private class ModuleInfo
        {
            public readonly IModule Module;
            public bool Initialized = false;
            public readonly Dictionary<Type, IModule> Dependencies = new Dictionary<Type, IModule>();

            public ModuleInfo(IModule module)
            {
                Module = module;

                foreach (Type t in module.ModuleDependencies)
                {
                    Dependencies.Add(t, null);
                }
            }

            public List<Type> ModulesNeeded
            {
                get
                {
                    List<Type> modulesNeeded = new List<Type>(Dependencies.Count);

                    foreach (KeyValuePair<Type, IModule> kvp in Dependencies)
                    {
                        if (kvp.Value != null)
                            continue;

                        modulesNeeded.Add(kvp.Key);
                    }

                    return modulesNeeded;
                }
            }
        }

        // all known modules
        private Dictionary<Type, ModuleInfo> _moduleTypeLookup = new Dictionary<Type, ModuleInfo>();
        private object _lockObj = new object();

        public ModuleManager()
        {
        }

        public void AddModule(IModule moduleToAdd)
        {
            ModuleInfo moduleInfo = new ModuleInfo(moduleToAdd);

            lock (_lockObj)
            {
                _moduleTypeLookup.Add(moduleToAdd.GetType(), moduleInfo);
            }
        }

        public void AddModule<TModule>(TModule moduleToAdd) where TModule : class, IModule
        {
            lock (_lockObj)
            {
                _moduleTypeLookup.Add(typeof(TModule), new ModuleInfo(moduleToAdd));
            }
        }
        
        public TModule GetModule<TModule>() where TModule : class, IModule
        {
            lock (_lockObj)
            {
                ModuleInfo moduleInfo;
                _moduleTypeLookup.TryGetValue(typeof(TModule), out moduleInfo);
                return moduleInfo.Module as TModule;
            }
        }
        
        public void RemoveModule(IModule moduleToRemove)
        {
            // TODO: add logic that checks that the module is not depended on still

            lock (_lockObj)
            {
                _moduleTypeLookup.Remove(moduleToRemove.GetType());
            }
        }

        public void LoadModules()
        {
            lock (_lockObj)
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
                        if (moduleInfo.Initialized)
                            continue;

                        List<Type> modulesNeeded = moduleInfo.ModulesNeeded;

                        // in order to load a module, we must first have all of its dependencies
                        // try to get all of the module's depedencies
                        for (int x = modulesNeeded.Count - 1; x >= 0; x--)
                        {
                            // try to find the dependency
                            ModuleInfo neededModuleInfo;
                            if (_moduleTypeLookup.TryGetValue(modulesNeeded[x], out neededModuleInfo) == true)
                            {
                                // dependency is only available if it is initialized (loaded)
                                if (neededModuleInfo.Initialized)
                                {
                                    moduleInfo.Dependencies[modulesNeeded[x]] = neededModuleInfo.Module;
                                    modulesNeeded.RemoveAt(x);
                                }
                            }
                        }

                        if (modulesNeeded.Count == 0)
                        {
                            // module has all of its dependencies, load it
                            System.Console.WriteLine("[ModuleManager] loading module: " + moduleInfo.Module.GetType().ToString());
                            if (moduleInfo.Module.Load(this, moduleInfo.Dependencies) == true)
                            {
                                moduleInfo.Initialized = true;
                                numModulesLoadedDuringLastPass++;
                            }
                            else
                            {
                                Console.WriteLine("[ModuleManager] failed to load module: " + moduleInfo.Module.GetType().ToString());
                            }
                        }
                        else
                        {
                            // module not ready to be initialized yet
                            numModulesLeftToLoad++;
                        }
                    }
                }
                while ((numModulesLeftToLoad > 0) && (numModulesLoadedDuringLastPass > 0));
            }

            // at this point, we loaded everything we could
            // anything left unloaded is missing at least one dependency
        }
    }
}

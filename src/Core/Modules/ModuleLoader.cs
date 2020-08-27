using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;
using System.IO;
using System.Xml;
using SS.Core.ComponentInterfaces;

namespace SS.Core.Modules
{
    public class ModuleLoader : IModule, IModuleLoader
    {
        private ModuleManager _mm;

        public ModuleLoader()
        {

        }

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get { return null; }

        }

        public bool Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;

            _mm.RegisterInterface<IModuleLoader>(this);

            return true;
        }

        public bool Unload(ModuleManager mm)
        {
            _mm.UnregisterInterface<IModuleLoader>();
            return true;
        }

        #endregion

        #region IModuleLoader Members

        bool IModuleLoader.LoadModulesFromConfig(string moduleConfigFilename)
        {
            XmlDocument xmlDoc = new XmlDocument();

            try
            {
                xmlDoc.Load(moduleConfigFilename);

                foreach (XmlNode node in xmlDoc.SelectNodes("modules/module"))
                {
                    XmlAttribute moduleNameAttribute = (XmlAttribute)node.Attributes.GetNamedItem("moduleName");
                    if (moduleNameAttribute == null)
                        continue; // attribute is required, ignore this node
                    
                    string moduleName = moduleNameAttribute.Value;
                    if(string.IsNullOrEmpty(moduleName))
                        continue;

                    XmlAttribute assemblyAttribute = (XmlAttribute)node.Attributes.GetNamedItem("assembly");
                    Assembly assembly = null;
                    if (assemblyAttribute != null)
                    {
                        string assemblyString = assemblyAttribute.Value;
                        if(string.IsNullOrEmpty(assemblyString) == false)
                        {
                            try
                            {
                                assembly = Assembly.Load(assemblyString);
                            }
                            catch
                            {
                                // unable to load assembly
                                continue;
                            }
                        }
                    }

                    if(assembly == null)
                    {
                        // no assembly specified, assume the current assembly
                        assembly = Assembly.GetExecutingAssembly();
                    }
                        
                    IModule module;
                    module = createModuleObject(assembly, moduleName);
                    if (module == null)
                        continue;

                    _mm.LoadModule(module);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            return true;
        }

        private IModule createModuleObject(Assembly assembly, string moduleFullName)
        {
            Type type = assembly.GetType(moduleFullName);
            if (type == null)
                return null;

            if (type.IsClass == false)
                return null;

            if (typeof(IModule).IsAssignableFrom(type))
            {
                IModule module = Activator.CreateInstance(type) as IModule;
                if (module != null)
                {
                    return module;
                }
            }

            return null;
        }

        bool IModuleLoader.AddModule(string assemblyString, string moduleFullName)
        {
            try
            {
                Assembly assembly = Assembly.Load(assemblyString);

                IModule module = createModuleObject(assembly, moduleFullName);
                return (module != null);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
        }

        void IModuleLoader.DoPostLoadStage()
        {
            _mm.EnumerateModules(delegate(IModule module, bool isLoaded)
            {
                if (isLoaded == false)
                    return;

                IModuleLoaderAware loaderAwareModule = module as IModuleLoaderAware;
                if (loaderAwareModule == null)
                    return;

                loaderAwareModule.PostLoad(_mm);
            });
        }

        void IModuleLoader.DoPreUnloadStage()
        {
            _mm.EnumerateModules(delegate(IModule module, bool isLoaded)
            {
                if (isLoaded == false)
                    return;

                IModuleLoaderAware loaderAwareModule = module as IModuleLoaderAware;
                if (loaderAwareModule == null)
                    return;

                loaderAwareModule.PreUnload(_mm);
            });
        }

        #endregion
    }
}

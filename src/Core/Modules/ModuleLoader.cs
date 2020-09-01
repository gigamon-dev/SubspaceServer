using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class ModuleLoader : IModule, IModuleLoader
    {
        private ModuleManager _mm;
        private ILogManager _logManager;

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

            if (_logManager != null)
            {
                _mm.ReleaseInterface<ILogManager>();
            }

            return true;
        }

        #endregion

        private void WriteLog(LogLevel level, string message)
        {
            if (_logManager == null)
            {
                _logManager = _mm.GetInterface<ILogManager>();
            }

            string logText = $"<{nameof(ModuleLoader)}> {message}";
            if (_logManager != null)
            {
                _logManager.Log(level, logText);
            }
            else
            {
                Console.WriteLine(logText);
            }
        }

        #region IModuleLoader Members

        bool IModuleLoader.LoadModulesFromConfig(string moduleConfigFilename)
        {
            try
            {
                // Read the xml config.
                var doc = XDocument.Load(moduleConfigFilename);
                IEnumerable<string> typeNames =
                    from moduleElement in doc.Descendants("module")
                    let name = moduleElement.Attribute("type").Value
                    where !string.IsNullOrWhiteSpace(name)
                    select name;

                // Try to load each module.
                foreach (string name in typeNames)
                {
                    _mm.AddModule(name);
                }

                // Tell the module manager to try to load everything that isn't already loaded.
                _mm.LoadAllModules();
            }
            catch (Exception ex)
            {
                WriteLog(LogLevel.Error, $"ModuleLoader.LoadModulesFromConfig: {ex}");
                return false;
            }

            return true;
        }

        #endregion
    }
}

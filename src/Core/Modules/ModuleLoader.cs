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
        private InterfaceRegistrationToken _iModuleLoaderToken;

        #region IModule Members

        Type[] IModule.InterfaceDependencies { get; } = null;

        bool IModule.Load(ModuleManager mm, IReadOnlyDictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;

            _iModuleLoaderToken = _mm.RegisterInterface<IModuleLoader>(this);
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            if (_mm.UnregisterInterface<IModuleLoader>(ref _iModuleLoaderToken) != 0)
                return false;

            return true;
        }

        #endregion

        private void WriteLog(LogLevel level, string message)
        {
            ILogManager _logManager = _mm.GetInterface<ILogManager>();

            if (_logManager != null)
            {
                try
                {
                    _logManager.LogM(level, nameof(ModuleLoader), message);
                }
                finally
                {
                    _mm.ReleaseInterface(ref _logManager);
                }
            }
            else
            {
                Console.WriteLine($"{(LogCode)level} <{nameof(ModuleLoader)}> {message}");
            }
        }

        #region IModuleLoader Members

        bool IModuleLoader.LoadModulesFromConfig(string moduleConfigFilename)
        {
            try
            {
                // Read the xml config.
                var doc = XDocument.Load(moduleConfigFilename);
                var moduleEntries =
                    from moduleElement in doc.Descendants("module")
                    let type = moduleElement.Attribute("type").Value
                    let path = moduleElement.Attribute("path")?.Value
                    let priority = moduleElement.Attribute("priority")?.Value
                    where !string.IsNullOrWhiteSpace(type)
                    select (type, path, priority);

                // Try to load each module.
                foreach (var entry in moduleEntries)
                {
                    if (string.Equals(entry.priority, "high", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(entry.path))
                            _mm.LoadModule(entry.type);
                        else
                            _mm.LoadModule(entry.type, entry.path);
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(entry.path))
                            _mm.AddModule(entry.type);
                        else
                            _mm.AddModule(entry.type, entry.path);
                    }
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

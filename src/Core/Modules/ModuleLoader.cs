using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that loads modules based on an xml configuration file.
    /// </summary>
    [CoreModuleInfo]
    public class ModuleLoader : IModule, IModuleLoader
    {
        private ComponentBroker _broker;
        private IModuleManager _mm;
        private InterfaceRegistrationToken _iModuleLoaderToken;

        #region IModule Members

        public bool Load(ComponentBroker broker, IModuleManager mm)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _mm = mm ?? throw new ArgumentNullException(nameof(mm));

            _iModuleLoaderToken = _broker.RegisterInterface<IModuleLoader>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (_broker.UnregisterInterface<IModuleLoader>(ref _iModuleLoaderToken) != 0)
                return false;

            return true;
        }

        #endregion

        private void WriteLog(LogLevel level, string message)
        {
            ILogManager _logManager = _broker.GetInterface<ILogManager>();

            if (_logManager != null)
            {
                try
                {
                    _logManager.LogM(level, nameof(ModuleLoader), message);
                }
                finally
                {
                    _broker.ReleaseInterface(ref _logManager);
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
                    if (string.IsNullOrWhiteSpace(entry.path))
                        _mm.LoadModule(entry.type);
                    else
                        _mm.LoadModule(entry.type, entry.path);
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

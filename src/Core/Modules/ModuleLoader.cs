using SS.Core.ComponentInterfaces;
using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that loads modules based on an xml configuration file.
    /// </summary>
    [CoreModuleInfo]
    public sealed class ModuleLoader(IModuleManager mm) : IModule, IModuleLoader
    {
        private readonly IModuleManager _mm = mm ?? throw new ArgumentNullException(nameof(mm));
        private InterfaceRegistrationToken<IModuleLoader>? _iModuleLoaderToken;

        #region IModule Members

        bool IModule.Load(IComponentBroker broker)
        {
            _iModuleLoaderToken = broker.RegisterInterface<IModuleLoader>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iModuleLoaderToken) != 0)
                return false;

            return true;
        }

        #endregion

        private void WriteLog(LogLevel level, string message)
        {
            TextWriter writer = (level == LogLevel.Error) ? Console.Error : Console.Out;
            writer.WriteLine($"{(LogCode)level} <{nameof(ModuleLoader)}> {message}");
        }

        private void WriteLog(LogLevel level, string message, IXmlLineInfo lineInfo)
        {
            if (lineInfo != null && lineInfo.HasLineInfo())
                WriteLog(level, $"{message} (Line {lineInfo.LineNumber}, Position {lineInfo.LinePosition})");
            else
                WriteLog(level, message);
        }

        #region IModuleLoader Members

        bool IModuleLoader.LoadModulesFromConfig(string moduleConfigFilename)
        {
            // Read the xml config.
            XDocument doc;

            try
            {
                doc = XDocument.Load(moduleConfigFilename, LoadOptions.SetLineInfo);
            }
            catch (Exception ex)
            {
                WriteLog(LogLevel.Error, $"Error reading xml config file '{moduleConfigFilename}'. {ex}");
                return false;
            }

            // Load modules based on the xml.
            try
            {
                var moduleEntries =
                    from moduleElement in doc.Descendants("module")
                    let type = moduleElement.Attribute("type")?.Value // null not allowed, but we'll check later
                    let path = moduleElement.Attribute("path")?.Value // non-null for plug-in modules only
                    let lineInfo = moduleElement as IXmlLineInfo
                    select (type, path, lineInfo);

                // Try to load each module.
                foreach (var (type, path, lineInfo) in moduleEntries)
                {
                    if (string.IsNullOrWhiteSpace(type))
                    {
                        WriteLog(LogLevel.Error, $"Missing/invalid 'type' attribute on 'module' element. Check the config: '{moduleConfigFilename}'.", lineInfo);
                        return false;
                    }

                    bool success;

                    if (string.IsNullOrWhiteSpace(path))
                        success = _mm.LoadModuleAsync(type).Result;
                    else
                        success = _mm.LoadModuleAsync(type, path).Result;

                    if (!success)
                    {
                        if (string.IsNullOrWhiteSpace(path))
                            WriteLog(LogLevel.Error, $"Failed to load '{type}'. Check the config: '{moduleConfigFilename}'.", lineInfo);
                        else
                            WriteLog(LogLevel.Error, $"Failed to load '{type}' [{path}]. Check the config: '{moduleConfigFilename}'.", lineInfo);

                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog(LogLevel.Error, $"Error loading modules. {ex}");
                return false;
            }

            return true;
        }

        #endregion
    }
}

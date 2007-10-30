using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core
{
    public class LogConsole : IModule
    {
        private ILogManager _logManager;

        private void logToConsole(string message)
        {
            if (_logManager.FilterLog(message, "log_console"))
                Console.WriteLine(message);
        }

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get
            {
                return new Type[] {
                    typeof(ILogManager)
                };
            }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IModuleInterface> interfaceDependencies)
        {
            _logManager = interfaceDependencies[typeof(ILogManager)] as ILogManager;
            if (_logManager == null)
                return false;

            mm.RegisterCallback<LogManager.LogDelegate>(LogManager.LogCallbackIdentifier, new LogManager.LogDelegate(logToConsole));

            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            mm.UnregisterCallback(LogManager.LogCallbackIdentifier, new LogManager.LogDelegate(logToConsole));
            mm.ReleaseInterface<ILogManager>();
            return true;
        }

        #endregion
    }
}

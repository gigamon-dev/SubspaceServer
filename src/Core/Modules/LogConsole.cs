using System;
using System.Collections.Generic;
using System.Text;
using SS.Core.ComponentInterfaces;
using SS.Core.ComponentCallbacks;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class LogConsole : IModule
    {
        private ILogManager _logManager;

        private void logToConsole(string message)
        {
            if (_logManager.FilterLog(message, "log_console"))
                Console.WriteLine(message);
        }

        #region IModule Members

        Type[] IModule.InterfaceDependencies { get; } = new Type[] 
        {
            typeof(ILogManager)
        };

        bool IModule.Load(ModuleManager mm, IReadOnlyDictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _logManager = interfaceDependencies[typeof(ILogManager)] as ILogManager;
            if (_logManager == null)
                return false;

            LogCallback.Register(mm, logToConsole);

            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            LogCallback.Unregister(mm, logToConsole);
            return true;
        }

        #endregion
    }
}

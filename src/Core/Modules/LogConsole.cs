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

        public bool Load(ComponentBroker broker, ILogManager logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

            LogCallback.Register(broker, logToConsole);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            LogCallback.Unregister(broker, logToConsole);
            return true;
        }

        #endregion
    }
}

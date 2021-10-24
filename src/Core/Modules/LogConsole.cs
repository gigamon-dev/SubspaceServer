using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class LogConsole : IModule
    {
        private ILogManager _logManager;

        private void LogToConsole(in LogEntry logEntry)
        {
            if (_logManager.FilterLog(in logEntry, "log_console"))
                Console.Out.WriteLine(logEntry.LogText);
        }

        #region IModule Members

        public bool Load(ComponentBroker broker, ILogManager logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            LogCallback.Register(broker, LogToConsole);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            LogCallback.Unregister(broker, LogToConsole);
            return true;
        }

        #endregion
    }
}

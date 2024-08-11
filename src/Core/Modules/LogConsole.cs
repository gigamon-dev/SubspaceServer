using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;

namespace SS.Core.Modules
{
    /// <summary>
    /// Logging module that to the console.
    /// </summary>
    [CoreModuleInfo]
    public class LogConsole : IModule
    {
        private ILogManager _logManager;

        public LogConsole(ILogManager logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }

        #region IModule Members

        bool IModule.Load(IComponentBroker broker)
        {
            
            LogCallback.Register(broker, LogToConsole);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            LogCallback.Unregister(broker, LogToConsole);
            return true;
        }

        #endregion

        private void LogToConsole(ref readonly LogEntry logEntry)
        {
            if (_logManager.FilterLog(in logEntry, "log_console"))
                Console.Out.WriteLine(logEntry.LogText);
        }
    }
}

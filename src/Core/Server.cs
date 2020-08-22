using System;
using System.Collections.Generic;
using System.Text;
using SS.Core.ComponentInterfaces;
using SS.Core.Modules;

namespace SS.Core
{
    /// <summary>
    /// note to self: in asss the main thread goes into a loop which processes timers sequentially.
    /// i am thinking that this server wont do that.  it'll be more like a windows service.
    /// </summary>
    public class Server
    {
        private LogManager _logManager;
        private ModuleManager _mm;

        public event EventHandler ServerStarted;
        public event EventHandler ServerStopped;

        public Server(string homeDirectory)
        {
            _mm = new ModuleManager();
            _logManager = new LogManager();

            _mm.LoadModule(_logManager);
        }

        public void Start()
        {
            loadModuleFile("conf/Modules.config");

            IModuleLoader loader = _mm.GetInterface<IModuleLoader>();
            if (loader == null)
                return;

            try
            {
                loader.DoPostLoadStage();
            }
            finally
            {
                _mm.ReleaseInterface<IModuleLoader>();
            }

            if (ServerStarted != null)
            {
                ServerStarted(this, EventArgs.Empty);
            }
        }

        public void Stop()
        {
            IModuleLoader loader = _mm.GetInterface<IModuleLoader>();
            if (loader == null)
                return;

            try
            {
                loader.DoPreUnloadStage();
            }
            finally
            {
                _mm.ReleaseInterface<IModuleLoader>();
            }

            _mm.UnloadAllModules();

            if (ServerStopped != null)
            {
                ServerStopped(this, EventArgs.Empty);
            }
        }

        private void loadModuleFile(string moduleConfigFilename)
        {
            // TODO: 
            // technically, if i just gave a directory to look for dlls in
            // i could use reflection to create the module objects
            // but i'm still thinking that it's better to read from a config file

            // TODO: read config file
            

            ModuleLoader moduleLoader = new ModuleLoader();
            _mm.LoadModule(moduleLoader);
            /*
            _mm.AddModule(new LogConsole());
            _mm.AddModule(new ConfigManager());
            _mm.AddModule(new Mainloop());
            _mm.AddModule(new PlayerData());
            _mm.AddModule(new ArenaManager());
            //_mm.AddModule(new Network());
            */
            IModuleLoader loader = _mm.GetInterface<IModuleLoader>();
            if (loader == null)
                return;

            try
            {
                loader.LoadModulesFromConfig(moduleConfigFilename);
                //loader.AddModuleModule("todo", "todo");
            }
            finally
            {
                _mm.ReleaseInterface<IModuleLoader>();
            }

            _mm.LoadAllModules();
        }
    }
}

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
        private ModuleManager _mm;

        public event EventHandler ServerStarted;
        public event EventHandler ServerStopped;

        public Server(string homeDirectory)
        {
            _mm = new ModuleManager();
        }

        public void Start()
        {
            LoadModuleFile("conf/Modules.config");
            _mm.DoPostLoadStage();

            ServerStarted?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            _mm.DoPreUnloadStage();
            _mm.UnloadAllModules();

            ServerStopped?.Invoke(this, EventArgs.Empty);
        }

        private void LoadModuleFile(string moduleConfigFilename)
        {
            IModuleLoader loader = _mm.GetInterface<IModuleLoader>();
            if (loader == null)
            {
                if (!_mm.LoadModule<ModuleLoader>())
                {
                    throw new Exception("Failed to load ModuleLoader.");
                }

                loader = _mm.GetInterface<IModuleLoader>();
                if (loader == null)
                {
                    throw new Exception("Loaded ModuleLoader, but unable to get it via its interface.");
                }
            }

            try
            {
                loader.LoadModulesFromConfig(moduleConfigFilename);
            }
            finally
            {
                _mm.ReleaseInterface<IModuleLoader>();
            }
        }
    }
}

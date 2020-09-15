using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
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
        private IMainloop _ml;
        private Task<int> _mainloopTask;

        public int Run()
        {
            if (_mm != null)
                throw new InvalidOperationException("Found an existing instance of ModuleManager.  Is the server already running?");

            _mm = new ModuleManager();
            LoadModuleFile("conf/Modules.config");
            _mm.DoPostLoadStage();

            _ml = _mm.GetInterface<IMainloop>();
            if (_ml == null)
                throw new Exception("Loaded modules, but unable to get IMainloop. Check that it is in the Modules.config.");

            int ret;

            try
            {
                ret = _ml.RunLoop();
            }
            finally
            {
                _mm.ReleaseInterface(ref _ml);
            }

            IChat chat = _mm.GetInterface<IChat>();
            if (chat != null)
            {
                try
                {
                    chat.SendArenaMessage(null, $"The server is {(ret == (int)ExitCode.Recycle ? "recycling" : "shutting down")} now!");
                }
                finally
                {
                    _mm.ReleaseInterface(ref chat);
                }
            }

            _mm.DoPreUnloadStage();
            _mm.UnloadAllModules();

            //_mm.Dispose(); // TODO
            _mm = null;

            return ret;
        }

        public void Quit()
        {
            _ml.Quit(ExitCode.None);
        }

        public void Start()
        {
            LoadModuleFile("conf/Modules.config");
            _mm.DoPostLoadStage();

            _ml = _mm.GetInterface<IMainloop>();
            if (_ml == null)
                throw new Exception("Loaded modules, but unable to get IMainloop. Check that it is in the Modules.config.");

            _mainloopTask = Task.Factory.StartNew(() => _ml.RunLoop(), TaskCreationOptions.LongRunning);
        }

        public int Stop()
        {
            _ml.Quit(ExitCode.None);
            int ret = _mainloopTask.Result;

            _mm.DoPreUnloadStage();
            _mm.UnloadAllModules();

            return ret;
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
                _mm.ReleaseInterface(ref loader);
            }
        }
    }
}

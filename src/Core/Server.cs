using SS.Core.ComponentInterfaces;
using SS.Core.Modules;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core
{
    /// <summary>
    /// A helpful abstraction of the server that assists with starting and stopping logic.
    /// <list type="number">
    /// <item>Start the server with <see cref="Run"/> or <see cref="RunAsync"/>.</item>
    /// <item>Use the <see cref="Broker"/> to access the various components of the server.</item>
    /// <item>Stop the server with <see cref="Quit"/>.</item>
    /// </list>
    /// </summary>
    public class Server
    {
        private volatile ModuleManager? _mm;

        /// <summary>
        /// The root broker.  Available after the server has been started. Otherwise, null.
        /// Use this to get the various <see cref="IComponentInterface"/>s that the loaded modules provide.
        /// </summary>
        public ComponentBroker? Broker { get { return _mm; } }

        /// <summary>
        /// Starts the server such that the calling thread runs the loop in the Mainloop module.
        /// This method will block until the server is told to stop (<see cref="Quit"/> or ?shutdown from in game).
        /// </summary>
        /// <returns>An exit code that can be returned to the OS.</returns>
        /// <exception cref="InvalidOperationException">The server is already running.</exception>
        public ExitCode Run()
        {
            if (!Start())
            {
                return ExitCode.ModLoad;
            }

            return RunMainloop();
        }

        /// <summary>
        /// Starts the server such that a worker thread (from the .NET thread pool) runs the loop in the Mainloop module.
        /// This method does not block. It could be useful if hosted in a Windows service, an interactive console app, or app with a GUI.
        /// </summary>
        /// <returns>A task representing the mainloop. Use its result to get the exit code.</returns>
        /// <exception cref="InvalidOperationException">The server is already running.</exception>
        public Task<ExitCode> RunAsync()
        {
            if (!Start())
            {
                return Task.FromResult(ExitCode.ModLoad);
            }

            return Task.Factory.StartNew(RunMainloop, TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// Tells the mainloop to quit.
        /// </summary>
        public void Quit()
        {
            ModuleManager? mm = _mm;
            if (mm is null)
            {
                Console.WriteLine("The server is not running.");
                return;
            }

            IMainloop? mainloop = mm.GetInterface<IMainloop>();
            if (mainloop is null)
            {
                Console.WriteLine("Mainloop is not loaded.");
                return;
            }

            try
            {
                mainloop.Quit(ExitCode.None);
            }
            finally
            {
                mm.ReleaseInterface(ref mainloop);
            }
        }

        private bool Start()
        {
            if (_mm is not null)
                throw new InvalidOperationException("The server is already running.");

            _mm = new ModuleManager();

            if (!LoadModulesFromConfig("conf/Modules.config"))
            {
                _mm.UnloadAllModules();
                return false;
            }

            _mm.DoPostLoadStage();

            return true;
        }

        private bool LoadModulesFromConfig(string moduleConfigFilename)
        {
            IModuleLoader? loader = _mm!.GetInterface<IModuleLoader>();
            if (loader is null)
            {
                if (!_mm.LoadModule<ModuleLoader>())
                {
                    Console.Error.WriteLine("Failed to load ModuleLoader.");
                    return false;
                }

                loader = _mm.GetInterface<IModuleLoader>();
                if (loader is null)
                {
                    Console.Error.WriteLine("Loaded ModuleLoader, but unable to get it via its interface.");
                    return false;
                }
            }

            try
            {
                return loader.LoadModulesFromConfig(moduleConfigFilename);
            }
            finally
            {
                _mm.ReleaseInterface(ref loader);
            }
        }

        private ExitCode RunMainloop()
        {
            ExitCode ret;

            // Run the mainloop.
            IMainloop? mainloop = _mm!.GetInterface<IMainloop>();
            if (mainloop is null)
            {
                Console.Error.WriteLine("Unable to get IMainloop. Check that the Mainloop module is in the Modules.config.");
                return ExitCode.General;
            }

            try
            {
                ret = mainloop.RunLoop();

                Console.WriteLine($"I <{nameof(Server)}> Exited main loop.");

                // Try to send a friendly message to anyone connected.
                // Note: There is no guarantee the Network module will send it before being unloaded.
                IChat? chat = _mm.GetInterface<IChat>();
                if (chat is not null)
                {
                    try
                    {
                        chat.SendArenaMessage(null, $"The server is {(ret == ExitCode.Recycle ? "recycling" : "shutting down")} now!");
                    }
                    finally
                    {
                        _mm.ReleaseInterface(ref chat);
                    }
                }

                IPersistExecutor? persistExecutor = _mm.GetInterface<IPersistExecutor>();
                if (persistExecutor is not null)
                {
                    try
                    {
                        Console.WriteLine($"I <{nameof(Server)}> Saving scores.");

                        using AutoResetEvent autoResetEvent = new(false);
                        persistExecutor.SaveAll(() => autoResetEvent.Set());

                        while (true)
                        {
                            if (autoResetEvent.WaitOne(10))
                                break;

                            mainloop.WaitForMainWorkItemDrain();
                        }
                    }
                    finally
                    {
                        _mm.ReleaseInterface(ref persistExecutor);
                    }
                }
            }
            finally
            {
                _mm.ReleaseInterface(ref mainloop);
            }

            // Unload.
            Console.WriteLine($"I <{nameof(Server)}> Unloading modules.");
            _mm.DoPreUnloadStage();
            _mm.UnloadAllModules();
            _mm = null;

            return ret;
        }
    }
}

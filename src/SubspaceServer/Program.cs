using SS.Core;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace SubspaceServer
{
    class Program
    {
        private static Server subspaceServer;
        private static bool isRunning = false;

        static void Main(string[] args)
        {
            string directory = null;

            if (args.Length > 0)
            {
                directory = args[0];

                if (!Directory.Exists(directory))
                {
                    directory = null;
                }
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Environment.CurrentDirectory;
            }

            /* HACK: 
             * To fix dynamically loading assemblies for modules that are not directly referenced.
             * The default .NET Core logic doesn't automatically search the directory the executable is in.
             * This does just that for the default AssemblyLoadContext.
             * 
             * Note:
             * If loaded into a separate AssemblyLoadContext, it would load another copy of SS.Core into it.
             * Then, the IModule interface type in the default context, would not work well with the copy of it in the other context.
             * That caused: !typeof(IModule).IsAssignableFrom(type)
             * To always return false.
             * 
             * For reference see: https://docs.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblyloadcontext
             */
            AssemblyLoadContext.Default.Resolving += ResolveAssembly;

            subspaceServer = new Server(directory);

            StartServer();
            MainMenu();
            StopServer(true);
        }

        private static Assembly ResolveAssembly(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            try
            {
                return context.LoadFromAssemblyPath(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, assemblyName.Name));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return null;
            }
        }

        private static void MainMenu()
        {
            while (true)
            {
                Console.Write(@"Menu
----
1. Start
2. Stop
X. Exit
> ");

                string input = Console.ReadLine();

                if (string.Equals(input, "x", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                
                int inputInt;
                if (!int.TryParse(input, out inputInt))
                    continue;

                switch (inputInt)
                {
                    case 1:
                        StartServer();
                        break;

                    case 2:
                        StopServer();
                        break;

                    default:
                        break;
                }
            }
        }

        private static void StartServer()
        {
            if (isRunning)
            {
                Console.WriteLine("Server is already started.");
                return;
            }

            subspaceServer.Start();
            isRunning = true;
            Console.WriteLine($"Started Server at {DateTime.Now.ToString("s")}");
        }

        private static void StopServer(bool skipWarning = false)
        {
            if (!isRunning)
            {
                if (!skipWarning)
                {
                    Console.WriteLine("Server is already stopped.");
                }
                return;
            }

            subspaceServer.Stop();
            isRunning = false;
            Console.WriteLine($"Stopped Server at {DateTime.Now.ToString("s")}");
        }
    }
}

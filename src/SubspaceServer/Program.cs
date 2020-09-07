using SS.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                foreach (string arg in args)
                {
                    if (string.Equals(arg, "-debug"))
                    {
                        PrintProbingProperties();
                    }
                    else
                    {
                        if (Directory.Exists(arg))
                        {
                            directory = arg;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Environment.CurrentDirectory;
            }

            subspaceServer = new Server(directory);

            StartServer();
            MainMenu();
            StopServer(true);
        }

        private static void PrintProbingProperties()
        {
            PrintProbingPaths("TRUSTED_PLATFORM_ASSEMBLIES");
            PrintProbingPaths("PLATFORM_RESOURCE_ROOTS");
            PrintProbingPaths("NATIVE_DLL_SEARCH_DIRECTORIES");
            PrintProbingPaths("APP_PATHS");
            PrintProbingPaths("APP_NI_PATHS");
        }

        private static void PrintProbingPaths(string propertyName)
        {
            Console.WriteLine(propertyName);
            Console.WriteLine(new string('-', propertyName.Length));
            
            string paths = AppContext.GetData(propertyName) as string;
            if (string.IsNullOrWhiteSpace(paths))
                return;

            IEnumerable<string> tokens = paths.Split(';', StringSplitOptions.RemoveEmptyEntries);
            tokens = tokens.OrderBy(s => s, StringComparer.Ordinal);
            foreach (var token in tokens)
                Console.WriteLine(token);
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
            Console.WriteLine($"Started Server at {DateTime.Now:s}");
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
            Console.WriteLine($"Stopped Server at {DateTime.Now:s}");
        }
    }
}

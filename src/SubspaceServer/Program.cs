using SS.Core;
using System;
using System.IO;

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

            subspaceServer = new Server(directory);

            StartServer();
            MainMenu();
            StopServer(true);
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

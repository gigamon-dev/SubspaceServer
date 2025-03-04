﻿using SS.Core;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace SubspaceServer
{
    class Program
    {
        private static Server? server;
        private static Task<ExitCode>? runTask;

        static int Main(string[] args)
        {
            bool interactiveMode = false;
            string? directory = null;

            if (args.Length > 0)
            {
                foreach (string arg in args)
                {
                    if (string.Equals(arg, "-debug", StringComparison.Ordinal))
                    {
                        PrintProbingProperties();
                    }
                    else if (string.Equals(arg, "-i", StringComparison.Ordinal))
                    {
                        interactiveMode = true;
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

            if (!string.IsNullOrWhiteSpace(directory))
            {
                try
                {
                    Directory.SetCurrentDirectory(directory);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error setting current directory. {ex.Message}");
                    return 1;
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // On Windows, the default timer resolution is not granular enough.
                // Sleep(1) will wait at least 15.625 ms (1000/64).
                // Request a 1 ms resolution time.
                uint result = UnmanagedWinMM.TimeBeginPeriod(1);
                if (result != UnmanagedWinMM.TIMERR_NOERROR)
                {
                    Console.WriteLine($"WARNING: Unable to set the minimum resolution time for periodic timers (result: {result}).");
                }
            }

            // Add the CodePagesEncodingProvider so that Windows-1252 can be used.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            Console.CancelKeyPress += Console_CancelKeyPress;

            try
            {
                if (!interactiveMode)
                {
                    // mainloop on the main thread
                    server = new Server();
                    return (int)server.Run();
                }
                else
                {
                    // mainloop in a worker thread
                    server = new Server();
                    StartServer();
                    MainMenu();
                    StopServer(true);
                    return 0;
                }
            }
            finally
            {
                Console.CancelKeyPress -= Console_CancelKeyPress;
            }
        }

        // This handles Ctrl + C (SIGINT) and Ctrl + Break (SIGBREAK).
        // SIGBREAK is Windows only (not a POSIX signal).
        private static void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            server?.Quit();
            e.Cancel = true; // The main thread should gracefully end.
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

            string? paths = AppContext.GetData(propertyName) as string;
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
3. Network Stats
X. Exit
> ");

                string? input = Console.ReadLine();

                if (string.Equals(input, "x", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!int.TryParse(input, out int inputInt))
                    continue;

                switch (inputInt)
                {
                    case 1:
                        StartServer();
                        break;

                    case 2:
                        StopServer();
                        break;

                    case 3:
                        PrintNetworkStats();
                        break;

                    default:
                        break;
                }
            }
        }

        private static void StartServer()
        {
            if (runTask != null && !runTask.IsCompleted)
            {
                Console.WriteLine("Server is already started.");
                return;
            }

            runTask = server!.RunAsync();

            Console.WriteLine($"Started Server at {DateTime.UtcNow:s}");

            runTask.ContinueWith(
                (task) =>
                {
                    Console.WriteLine($"Server stopped at {DateTime.UtcNow:s} with exit code:{task.Result}");
                });
        }

        private static void StopServer(bool skipWarning = false)
        {
            if (runTask == null || runTask.IsCompleted)
            {
                if (!skipWarning)
                {
                    Console.WriteLine("Server is already stopped.");
                }

                return;
            }

            Console.WriteLine($"Stopping the server...");
            server!.Quit();
            runTask.Wait();
            runTask = null;
        }

        private static void PrintNetworkStats()
        {
            ComponentBroker? broker = server!.Broker;
            if (broker == null)
                return;

            INetwork? network = broker.GetInterface<INetwork>();
            if (network == null)
                return;

            try
            {
                var stats = network.GetStats();

                Console.WriteLine($"netstats: pings={stats.PingsReceived}");
                Console.WriteLine($"netstats: sent bytes={stats.BytesSent} packets={stats.PacketsSent}");
                Console.WriteLine($"netstats: received bytes={stats.BytesReceived} packets={stats.PacketsReceived}");
                Console.WriteLine($"netstats: buffers used={stats.BuffersUsed}/{stats.BuffersTotal} ({(double)stats.BuffersUsed / stats.BuffersTotal:p})");

                Console.WriteLine($"netstats: grouped=" +
                    $"{stats.GroupedStats0}/" +
                    $"{stats.GroupedStats1}/" +
                    $"{stats.GroupedStats2}/" +
                    $"{stats.GroupedStats3}/" +
                    $"{stats.GroupedStats4}/" +
                    $"{stats.GroupedStats5}/" +
                    $"{stats.GroupedStats6}/" +
                    $"{stats.GroupedStats7}");

                Console.WriteLine($"netstats: pri=" +
                    $"{stats.PriorityStats0}/" +
                    $"{stats.PriorityStats1}/" +
                    $"{stats.PriorityStats2}/" +
                    $"{stats.PriorityStats3}/" +
                    $"{stats.PriorityStats4}");
            }
            finally
            {
                broker.ReleaseInterface(ref network);
            }
        }
    }

    
    internal unsafe static partial class UnmanagedWinMM
    {
        /// <summary>
        /// Requests a minimum resolution for periodic timers.
        /// </summary>
        /// <remarks>
        /// https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod
        /// </remarks>
        /// <param name="uPeriod">The minimum timer resolution, in milliseconds.</param>
        /// <returns></returns>
        [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        internal static partial uint TimeBeginPeriod(uint uPeriod);

        private const uint TIMERR_BASE = 96;
        internal const uint TIMERR_NOERROR = 0;
        internal const uint TIMERR_NOCANDO = (TIMERR_BASE + 1);
    }
}

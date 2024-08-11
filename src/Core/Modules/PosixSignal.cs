using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SS.Core.Modules
{
    /// <summary>
    /// This module watches for certain POSIX signals and processes them accordingly.
    /// <list type="table">
    /// <item>
    ///     <term>SIGHUP</term>
    ///     <description>Reopens the <see cref="ILogFile"/> log file and submits a request to the <see cref="IPersistExecutor"/> to save.</description>
    /// </item>
    /// <item>
    ///     <term>SIGINT and SIGTERM</term>
    ///     <description>Signals the <see cref="IMainloop"/> to stop.</description>
    /// </item>
    /// <item>
    ///     <term>SIGUSR1</term>
    ///     <description>Submits a request to the <see cref="IPersistExecutor"/> to save.</description>
    /// </item>
    /// <item>
    ///     <term>SIGUSR2</term>
    ///     <description>Looks 
    ///     for a file named "MESSAGE" in the zone's root path. 
    ///     If it exists, sends the first line of text from the file as an arena chat message to all arenas and deletes the file.</description>
    /// </item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// This is the equivalent of the unixsignal module in ASSS.
    /// However, it is implemented a little differently.
    /// ASSS processes the signal on the mainloop thread,
    /// whereas here it's processed on a worker thread.
    /// This is better since the processing logic involves blocking I/O in certain cases
    /// such as reopening the log file for SIGHUP and opening/deleting the "MESSAGE" file for SIGUSR2.
    /// </remarks>
    public class PosixSignal : IModule
    {
        private readonly IComponentBroker _broker;

        private PosixSignalRegistration? _sighup;
        private PosixSignalRegistration? _sigint;
        private PosixSignalRegistration? _sigterm;
        private PosixSignalRegistration? _sigusr1;
        private PosixSignalRegistration? _sigusr2;

        /// <summary>
        /// User-defined signal 1
        /// </summary>
        private const int SIGUSR1 = 10;

        /// <summary>
        /// User-defined signal 2
        /// </summary>
        private const int SIGUSR2 = 12;

        public PosixSignal(IComponentBroker broker)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _sighup = PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, PosixSignalHandler_SIGHUP);
            _sigint = PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, PosixSignalHandler_SIGINT);
            _sigterm = PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, PosixSignalHandler_SIGTERM);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _sigusr1 = PosixSignalRegistration.Create((System.Runtime.InteropServices.PosixSignal)SIGUSR1, PosixSignalHandler_SIGUSR1);
                _sigusr2 = PosixSignalRegistration.Create((System.Runtime.InteropServices.PosixSignal)SIGUSR2, PosixSignalHandler_SIGUSR2);
            }

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            _sighup?.Dispose();
            _sighup = null;

            _sigint?.Dispose();
            _sigint = null;

            _sigterm?.Dispose();
            _sigterm = null;

            _sigusr1?.Dispose();
            _sigusr1 = null;

            _sigusr2?.Dispose();
            _sigusr2 = null;

            return true;
        }

        #endregion

        #region POSIX Signal Handlers

        private void PosixSignalHandler_SIGHUP(PosixSignalContext context)
        {
            context.Cancel = true;

            ILogFile? logfile = _broker.GetInterface<ILogFile>();
            if (logfile is not null)
            {
                try
                {
                    logfile.Reopen();
                }
                finally
                {
                    _broker.ReleaseInterface(ref logfile);
                }
            }

            RequestPersistToSave();
        }

        private void PosixSignalHandler_SIGINT(PosixSignalContext context)
        {
            context.Cancel = true;

            IMainloop? mainloop = _broker.GetInterface<IMainloop>();
            if (mainloop is not null)
            {
                try
                {
                    mainloop.Quit(ExitCode.None);
                }
                finally
                {
                    _broker.ReleaseInterface(ref mainloop);
                }
            }
        }

        private void PosixSignalHandler_SIGTERM(PosixSignalContext context)
        {
            PosixSignalHandler_SIGINT(context);
        }

        private void PosixSignalHandler_SIGUSR1(PosixSignalContext context)
        {
            context.Cancel = true;
            RequestPersistToSave();
        }

        private void PosixSignalHandler_SIGUSR2(PosixSignalContext context)
        {
            context.Cancel = true;

            const string filename = "MESSAGE";

            Span<char> buffer = stackalloc char[ChatPacket.MaxMessageChars];

            try
            {
                using StreamReader reader = new(filename, StringUtils.DefaultEncoding);
                int numChars = reader.Read(buffer);
                buffer = buffer[..numChars];
            }
            catch
            {
                return;
            }

            if (!buffer.IsEmpty)
            {
                int index = buffer.IndexOfAny('\r', '\n');
                if (index != -1)
                {
                    buffer = buffer[..index];
                }

                if (!buffer.IsEmpty)
                {
                    IChat? chat = _broker.GetInterface<IChat>();
                    if (chat is not null)
                    {
                        try
                        {
                            chat.SendArenaMessage(null, buffer);
                        }
                        finally
                        {
                            _broker.ReleaseInterface(ref chat);
                        }
                    }
                }
            }

            try
            {
                File.Delete(filename);
            }
            catch
            {
                return;
            }
        }

        #endregion

        private void RequestPersistToSave()
        {
            IPersistExecutor? persistExector = _broker.GetInterface<IPersistExecutor>();
            if (persistExector is not null)
            {
                try
                {
                    persistExector.SaveAll(null);
                }
                finally
                {
                    _broker.ReleaseInterface(ref persistExector);
                }
            }
        }
    }
}

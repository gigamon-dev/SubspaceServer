using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides the Chat module with functionality to filter obscenities in chat messages.
    /// </summary>
    /// <remarks>
    /// Rather than how ASSS uses a config setting to store words, this implementation uses a separate 'obscene.txt' file, similar to subgame.
    /// </remarks>
    [CoreModuleInfo]
    public sealed class Obscene : IModule, IObscene, IDisposable
    {
        private ComponentBroker _broker;
        private ICommandManager _commandManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;

        private InterfaceRegistrationToken<IObscene> _iObsceneRegistrationToken;

        private const string ObsceneFileName = "obscene.txt";
        private const string Replace = "%@$&%*!#@&%!#&*$#?@!*%@&!%#&%!?$*#!*$&@#&%$!*%@#&%!@&#$!*@&$%*@?";

        private ulong _replaceCount = 0;
        private FileSystemWatcher _fileSystemWatcher;

        private readonly ReaderWriterLockSlim _rwLock = new();
        private int? _checksum = null;
        private List<string> _obsceneList = null;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            ICommandManager commandManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloop mainloop)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

            _fileSystemWatcher = new(".", ObsceneFileName);
            _fileSystemWatcher.NotifyFilter = NotifyFilters.LastWrite;
            _fileSystemWatcher.Changed += FileSystemWatcher_Changed;
            _fileSystemWatcher.EnableRaisingEvents = true;

            mainloop.QueueThreadPoolWorkItem(_ => LoadObscene(), (object)null);

            _commandManager.AddCommand("obscene", Command_obscene);
            _iObsceneRegistrationToken = broker.RegisterInterface<IObscene>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iObsceneRegistrationToken) != 0)
                return false;

            _commandManager.RemoveCommand("obscene", Command_obscene);

            _rwLock.EnterWriteLock();

            try
            {
                _checksum = null;
                _obsceneList = null;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            return true;
        }

        #endregion

        #region IObscene

        bool IObscene.Filter(Span<char> line)
        {
            List<string> obsceneList;

            _rwLock.EnterReadLock();

            try
            {
                obsceneList = _obsceneList;
            }
            finally
            {
                _rwLock.ExitReadLock();
            }

            bool filtered = false;

            if (obsceneList != null)
            {
                foreach (string obscenity in obsceneList)
                {
                    int index;
                    
                    while ((index = MemoryExtensions.IndexOf(line, obscenity, StringComparison.OrdinalIgnoreCase)) != -1)
                    {
                        filtered = true;

                        Span<char> toReplace = line.Slice(index, obscenity.Length);
                        for (int charIndex = 0; charIndex < toReplace.Length; charIndex++)
                        {
                            ulong replaceCount = Interlocked.Increment(ref _replaceCount);
                            toReplace[charIndex] = Replace[(int)replaceCount++ % Replace.Length];
                        }
                    }
                }
            }

            return filtered;
        }

        #endregion

        private void Command_obscene(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            player.Flags.ObscenityFilter = !player.Flags.ObscenityFilter;

            IChat chat = _broker.GetInterface<IChat>();
            if (chat != null)
            {
                try
                {
                    chat.SendMessage(player, $"Obscenity filter {(player.Flags.ObscenityFilter ? "ON" : "OFF")}");
                }
                finally
                {
                    _broker.ReleaseInterface(ref chat);
                }
            }
        }

        private void FileSystemWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            LoadObscene();
        }

        // NOTE: This is always run on a worker thread.
        private void LoadObscene()
        {
            _rwLock.EnterUpgradeableReadLock();

            try
            {

                int checksum;
                List<string> obsceneList;

                try
                {
                    if (!File.Exists(ObsceneFileName))
                        return;

                    //
                    // Open the file.
                    //

                    FileStream fs = null;
                    int tries = 0;

                    do
                    {
                        try
                        {
                            fs = File.OpenRead(ObsceneFileName);
                        }
                        catch (IOException ex)
                        {
                            // Note: This retry logic is to workaround the "The process cannot access the file because it is being used by another process." race condition.
                            if (++tries >= 5)
                            {
                                _logManager.LogM(LogLevel.Error, nameof(Obscene), $"Error opening {ObsceneFileName} ({tries} tries). {ex.Message}");
                                return;
                            }

                            _logManager.LogM(LogLevel.Drivel, nameof(MapNewsDownload), $"Error opening {ObsceneFileName} ({tries} tries). {ex.Message}");

                            Thread.Sleep(1000);
                        }
                        catch (Exception ex)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(Obscene), $"Error opening {ObsceneFileName}. {ex.Message}");
                            return;
                        }
                    }
                    while (fs == null);

                    using StreamReader sr = new(fs, StringUtils.DefaultEncoding);

                    //
                    // Checksum
                    //

                    Ionic.Crc.CRC32 crc32 = new();
                    checksum = crc32.GetCrc32(fs);

                    if (_checksum != null && _checksum == checksum)
                        return; // no change

                    fs.Position = 0;

                    //
                    // Allocate the list.
                    //

                    int lineCount = GetLineCount(sr);
                    obsceneList = new(lineCount);

                    fs.Position = 0;

                    //
                    // Populate the list.
                    //

                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length > 0 && !line.StartsWith('#'))
                        {
                            obsceneList.Add(line);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Obscene), $"Error loading {ObsceneFileName}. {ex.Message}");
                    return;
                }

                _rwLock.EnterWriteLock();

                try
                {
                    _checksum = checksum;
                    _obsceneList = obsceneList;
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }

                _logManager.LogM(LogLevel.Info, nameof(Obscene), $"Loaded {ObsceneFileName} (words:{obsceneList.Count}, checksum:{_checksum:X}).");
            }
            finally
            {
                _rwLock.ExitUpgradeableReadLock();
            }
        }

        private int GetLineCount(StreamReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));

            int count = 0;
            Span<char> buffer = stackalloc char[512];

            int charsRead;
            while ((charsRead = reader.Read(buffer)) > 0)
            {
                for (int i = 0; i < charsRead; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        count++;
                    }
                }
            }

            return count + 1;
        }

        public void Dispose()
        {
            if (_fileSystemWatcher != null)
            {
                _fileSystemWatcher.Changed -= FileSystemWatcher_Changed;
                _fileSystemWatcher.Dispose();
                _fileSystemWatcher = null;
            }
        }
    }
}

using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides the Chat module with functionality to filter obscenities in chat messages.
    /// </summary>
    /// <remarks>
    /// Rather than how ASSS uses a config setting to store words, this implementation uses a separate 'obscene.txt' file, similar to subgame.
    /// </remarks>
    [CoreModuleInfo]
    public sealed class Obscene(
        IComponentBroker broker,
        ICommandManager commandManager,
        ILogManager logManager,
        IMainloop mainloop,
        IObjectPoolManager objectPoolManager) : IAsyncModule, IObscene, IDisposable
    {
        private readonly IComponentBroker _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        private readonly ICommandManager _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IMainloop _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

        private InterfaceRegistrationToken<IObscene>? _iObsceneRegistrationToken;

        private const string ObsceneFileName = "obscene.txt";
        private const string Replace = "%@$&%*!#@&%!#&*$#?@!*%@&!%#&%!?$*#!*$&@#&%$!*%@#&%!@&#$!*@&$%*@?";

        private ulong _replaceCount = 0;
        private FileSystemWatcher? _fileSystemWatcher;

        private readonly object _loadLock = new();
        private bool _isLoadRequested = false;
        private Task? _loadTask = null;

        private readonly object _obsceneLock = new();
        private uint? _checksum = null;
        private List<string>? _obsceneList = null;
        private SearchValues<string>? _obscenities = null;

        #region Module members

        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _fileSystemWatcher = new(".", ObsceneFileName);
            _fileSystemWatcher.NotifyFilter = NotifyFilters.LastWrite;
            _fileSystemWatcher.Changed += FileSystemWatcher_Changed;
            _fileSystemWatcher.EnableRaisingEvents = true;

            await QueueLoadObscene().ConfigureAwait(false);

            _commandManager.AddCommand("obscene", Command_obscene);
            _iObsceneRegistrationToken = broker.RegisterInterface<IObscene>(this);
            return true;
        }

        Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (broker.UnregisterInterface(ref _iObsceneRegistrationToken) != 0)
                return Task.FromResult(false);

            _commandManager.RemoveCommand("obscene", Command_obscene);

            lock (_obsceneLock)
            {
                _checksum = null;
                _obsceneList = null;
            }

            return Task.FromResult(true);
        }

        #endregion

        #region IObscene

        bool IObscene.Filter(Span<char> line)
        {
			List<string>? obsceneList;
			SearchValues<string>? obscenities;

            lock (_obsceneLock)
            {
				obsceneList = _obsceneList;
				obscenities = _obscenities;
            }

			bool filtered = false;

			if (obsceneList is not null && obscenities is not null)
            {
                while (!line.IsEmpty)
                {
					int index = line.IndexOfAny(obscenities);
					if (index == -1)
                        break;

                    line = line[index..];

                    foreach (string obscenity in obsceneList)
                    {
                        if (line.StartsWith(obscenity))
                        {
							Span<char> toReplace = line[..obscenity.Length];
							for (int charIndex = 0; charIndex < toReplace.Length; charIndex++)
							{
								ulong replaceCount = Interlocked.Increment(ref _replaceCount);
								toReplace[charIndex] = Replace[(int)replaceCount % Replace.Length];
							}

                            filtered = true;
							line = line[obscenity.Length..];
                            break;
						}
                    }
				}
            }

            return filtered;
        }

        ulong IObscene.ReplaceCount => Interlocked.Read(ref _replaceCount);

        #endregion

        private void Command_obscene(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            player.Flags.ObscenityFilter = !player.Flags.ObscenityFilter;

            IChat? chat = _broker.GetInterface<IChat>();
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
            QueueLoadObscene();
        }

        private Task QueueLoadObscene()
        {
            lock (_loadLock)
            {
                _isLoadRequested = true;
                return _loadTask ??= Task.Run(LoadObsceneAsync);
            }
        }

        private async Task LoadObsceneAsync()
        {
            while (true)
            {
                lock (_loadLock)
                {
                    if (!_isLoadRequested)
                    {
                        _loadTask = null;
                        break;
                    }

                    _isLoadRequested = false;
                }

                await ProcessObsceneAsync().ConfigureAwait(false);
            }

            async Task ProcessObsceneAsync()
            {
                uint checksum;
                List<string> obsceneList;

                try
                {
                    //
                    // Open the file.
                    //

                    FileStream? fileStream = null;
                    int tries = 0;

                    do
                    {
                        try
                        {
                            fileStream = new FileStream(ObsceneFileName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                        }
                        catch (IOException ex) when (ex is not FileNotFoundException && ex is not DirectoryNotFoundException)
                        {
                            // Note: This retry logic is to workaround the "The process cannot access the file because it is being used by another process." race condition.
                            if (++tries >= 10)
                            {
                                _logManager.LogM(LogLevel.Error, nameof(Obscene), $"Error opening {ObsceneFileName} ({tries} tries). {ex.Message}");
                                return;
                            }

                            _logManager.LogM(LogLevel.Drivel, nameof(MapNewsDownload), $"Error opening {ObsceneFileName} ({tries} tries). {ex.Message}");

                            await Task.Delay(1000).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(Obscene), $"Error opening {ObsceneFileName}. {ex.Message}");
                            return;
                        }
                    }
                    while (fileStream is null);

                    try
                    {
                        //
                        // Checksum
                        //

                        Crc32 crc32 = _objectPoolManager.Crc32Pool.Get();

                        try
                        {
                            await crc32.AppendAsync(fileStream).ConfigureAwait(false);
                            checksum = crc32.GetCurrentHashAsUInt32();
                        }
                        finally
                        {
                            _objectPoolManager.Crc32Pool.Return(crc32);
                        }

                        bool isUnchanged;

                        lock (_obsceneLock)
                        {
                            isUnchanged = _checksum is not null && _checksum == checksum;
                        }

                        if (isUnchanged)
                        {
                            _logManager.LogM(LogLevel.Drivel, nameof(Obscene), $"Checked {ObsceneFileName}, but there was no change (checksum {checksum:X})");
                            return; // no change
                        }

                        fileStream.Position = 0;

                        //
                        // Allocate the list.
                        //

                        using StreamReader sr = new(fileStream, StringUtils.DefaultEncoding, true, -1, true);
                        int lineCount = await GetLineCountAsync(sr).ConfigureAwait(false);
                        obsceneList = new(lineCount);

                        fileStream.Position = 0;

                        //
                        // Populate the list.
                        //

                        string? line;
                        while ((line = await sr.ReadLineAsync(CancellationToken.None)) != null)
                        {
                            if (line.Length > 0 && !line.StartsWith('#'))
                            {
                                obsceneList.Add(line);
                            }
                        }
                    }
                    finally
                    {
                        await fileStream.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Obscene), $"Error loading {ObsceneFileName}. {ex.Message}");
                    return;
                }

                lock (_obsceneLock)
                {
                    _checksum = checksum;
                    _obsceneList = obsceneList;
                    _obscenities = SearchValues.Create(CollectionsMarshal.AsSpan(obsceneList), StringComparison.OrdinalIgnoreCase);
                }

                _logManager.LogM(LogLevel.Info, nameof(Obscene), $"Loaded {ObsceneFileName} (words:{obsceneList.Count}, checksum:{_checksum:X}).");
            }
        }

        private static async Task<int> GetLineCountAsync(StreamReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            char[] buffer = ArrayPool<char>.Shared.Rent(512);
            try
            {
                int count = 0;
                int charsRead;
                while ((charsRead = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
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
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }

        public void Dispose()
        {
            if (_fileSystemWatcher != null)
            {
                _fileSystemWatcher.EnableRaisingEvents = false;
                _fileSystemWatcher.Changed -= FileSystemWatcher_Changed;
                _fileSystemWatcher.Dispose();
                _fileSystemWatcher = null;
            }
        }
    }
}

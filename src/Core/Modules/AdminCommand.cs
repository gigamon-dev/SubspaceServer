using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that handles commands for administrators.
    /// </summary>
    [CoreModuleInfo]
    public sealed class AdminCommand(
        IArenaManager arenaManager,
        ICapabilityManager capabilityManager,
        IChat chat,
        IClientSettings clientSettings,
        IConfigManager configManager,
        ICommandManager commandManager,
        IFileTransfer fileTransfer,
        ILogFile logFile,
        ILogManager logManager,
        IMainloop mainloop,
        INetwork network,
        IPlayerData playerData) : IModule
    {
        private const string MapUploadDirectory = "maps/upload";

        /// <summary>
        /// The maximum # of lines of chat messages to send at once before delaying to send more.
        /// </summary>
        private const int MaxChatMessagesAtOnce = 20;

        private readonly IArenaManager _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
        private readonly ICapabilityManager _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly IClientSettings _clientSettings = clientSettings ?? throw new ArgumentNullException(nameof(clientSettings));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ICommandManager _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        private readonly IFileTransfer _fileTransfer = fileTransfer ?? throw new ArgumentNullException(nameof(fileTransfer));
        private readonly ILogFile _logFile = logFile ?? throw new ArgumentNullException(nameof(logFile));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IMainloop _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
        private readonly INetwork _network = network ?? throw new ArgumentNullException(nameof(network));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _commandManager.AddCommand("admlogfile", Command_admlogfile);
            _commandManager.AddCommand("ls", Command_ls);
            _commandManager.AddCommand("getfile", Command_getfile);
            _commandManager.AddCommand("putfile", Command_putFileOrZip);
            _commandManager.AddCommand("putzip", Command_putFileOrZip);
            _commandManager.AddCommand("putmap", Command_putmap);
            _commandManager.AddCommand("makearena", Command_makearena);
            _commandManager.AddCommand("botfeature", Command_botfeature);
            _commandManager.AddCommand("cd", Command_cd);
            _commandManager.AddCommand("pwd", Command_pwd);
            _commandManager.AddCommand("delfile", Command_delfile);
            _commandManager.AddCommand("renfile", Command_renfile);
            _commandManager.AddCommand("ufo", Command_ufo);
            _commandManager.AddCommand("super", Command_super);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            _commandManager.RemoveCommand("admlogfile", Command_admlogfile);
            _commandManager.RemoveCommand("ls", Command_ls);
            _commandManager.RemoveCommand("getfile", Command_getfile);
            _commandManager.RemoveCommand("putfile", Command_putFileOrZip);
            _commandManager.RemoveCommand("putzip", Command_putFileOrZip);
            _commandManager.RemoveCommand("putmap", Command_putmap);
            _commandManager.RemoveCommand("makearena", Command_makearena);
            _commandManager.RemoveCommand("botfeature", Command_botfeature);
            _commandManager.RemoveCommand("cd", Command_cd);
            _commandManager.RemoveCommand("pwd", Command_pwd);
            _commandManager.RemoveCommand("delfile", Command_delfile);
            _commandManager.RemoveCommand("renfile", Command_renfile);
            _commandManager.RemoveCommand("ufo", Command_ufo);
            _commandManager.RemoveCommand("super", Command_super);

            return true;
        }

        #endregion

        #region Commands

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "{flush | reopen}",
            Description = """
                Administers the log file that the server keeps. There are two possible
                subcommands: {flush} flushes the log file to disk (in preparation for
                copying it, for example), and {reopen} tells the server to close and
                re-open the log file (to rotate the log while the server is running).
                """)]
        private void Command_admlogfile(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (parameters.Equals("flush", StringComparison.OrdinalIgnoreCase))
            {
                // Do it on a worker thread, don't want to block the mainloop thread.
                _mainloop.QueueThreadPoolWorkItem<object?>(ThreadPoolWorkItem_Flush, null);
                _chat.SendMessage(player, "Flushing log file.");
            }
            else if (parameters.Equals("reopen", StringComparison.OrdinalIgnoreCase))
            {
                // Do it on a worker thread, don't want to block the mainloop thread.
                _mainloop.QueueThreadPoolWorkItem<object?>(ThreadPoolWorkItem_Reopen, null);
                _chat.SendMessage(player, "Reopening log file.");
            }

            void ThreadPoolWorkItem_Flush(object? dummy)
            {
                _logFile.Flush();
            }

            void ThreadPoolWorkItem_Reopen(object? dummy)
            {
                _logFile.Reopen();
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<server path>",
            Description = """
                List directory contents of a server-side working directory
                The path is relative to the current working directory.
                """)]
        private void Command_ls(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            string? workingDir = _fileTransfer.GetWorkingDirectory(player);
            if (workingDir is null)
                return;

            string path = Path.Join(workingDir, parameters);
            string fullPath = Path.GetFullPath(path);
            string currentDir = Directory.GetCurrentDirectory();

            if (!IsWithinBasePath(fullPath, currentDir))
            {
                _chat.SendMessage(player, "Invalid path.");
                return;
            }

            ListAsync(player.Name!, fullPath);

            // async local function since the command handler can't be made async
            async void ListAsync(string playerName, string path)
            {
                try
                {
                    DirectoryInfo directory = await Task.Run(() => new DirectoryInfo(path)).ConfigureAwait(false);
                    int lineCount = 0;

                    // List subdirectories.
                    using (var enumerator = await Task.Run(() => directory.EnumerateDirectories().GetEnumerator()).ConfigureAwait(false))
                    {
                        while (await Task.Run(() => enumerator.MoveNext()).ConfigureAwait(false))
                        {
                            // Add a delay to prevent sending too many messages at once (max outgoing limit in the Network module).
                            if (++lineCount % MaxChatMessagesAtOnce == 0)
                                await Task.Delay(500).ConfigureAwait(false);

                            // The player could have disconnected.
                            Player? player = _playerData.FindPlayer(playerName);
                            if (player is null)
                                return;

                            DirectoryInfo directoryInfo = enumerator.Current;
                            _chat.SendMessage(player, $"{"<DIR>",10} {directoryInfo.LastWriteTimeUtc:u} {directoryInfo.Name}");
                        }
                    }

                    // List files.
                    using (var enumerator = await Task.Run(() => directory.EnumerateFiles().GetEnumerator()).ConfigureAwait(false))
                    {
                        while (await Task.Run(() => enumerator.MoveNext()).ConfigureAwait(false))
                        {
                            // Add a delay to prevent sending too many messages at once (max outgoing limit in the Network module).
                            if (++lineCount % MaxChatMessagesAtOnce == 0)
                                await Task.Delay(500).ConfigureAwait(false);

                            // The player could have disconnected.
                            Player? player = _playerData.FindPlayer(playerName);
                            if (player is null)
                                return;

                            FileInfo fileInfo = enumerator.Current;
                            _chat.SendMessage(player, $"{fileInfo.Length,10} {fileInfo.LastWriteTimeUtc:u} {fileInfo.Name}");
                        }
                    }

                    _chat.SendMessage(player, $"total {lineCount}");
                }
                catch (Exception ex)
                {
                    Player? player = _playerData.FindPlayer(playerName);
                    if (player is not null)
                    {
                        _logManager.LogP(LogLevel.Warn, nameof(AdminCommand), player, $"Command_ls exception: {ex}");
                        _chat.SendMessage(player, $"ls: {ex.Message}");
                    }
                    else
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(AdminCommand), $"[{playerName}] Command_ls exception: {ex}");
                    }                    
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<filename>",
            Description = """
                Transfers the specified file from the server to the client. The filename
                is considered relative to the current working directory.
                """)]
        private void Command_getfile(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.IsStandard)
            {
                _chat.SendMessage(player, "Your client does not support file transfers.");
                return;
            }

            string? workingDir = _fileTransfer.GetWorkingDirectory(player);
            if (workingDir is null)
                return;

            string path = Path.Join(workingDir, parameters);
            string fullPath = Path.GetFullPath(path);
            string currentDir = Directory.GetCurrentDirectory();

            if (!IsWithinBasePath(fullPath, currentDir))
            {
                _chat.SendMessage(player, "Invalid path.");
            }
            else
            {
                string relativePath = Path.GetRelativePath(currentDir, fullPath);
                SendFileAsync(player, relativePath);
            }

            // async local helper since the command handler cannot be made async
            async void SendFileAsync(Player player, string path)
            {
                // Save the player before the await, so that we can check if the player object after the await.
                string playerName = player.Name!;

                bool queued = await _fileTransfer.SendFileAsync(player, path, Path.GetFileName(path).AsMemory(), false).ConfigureAwait(true);

                // The player's state could have changed (e.g. disconnect) by the time the await continuation executes.
                // Check that the player is still valid.
                if (player != _playerData.FindPlayer(playerName))
                    return;

                if (queued)
                {
                    _chat.SendMessage(player, $"Sending '{path}'.");
                }
                else
                {
                    _chat.SendMessage(player, $"Error sending '{path}'.");
                }
            }
        }

        [CommandHelp(
            Command = "putfile",
            Targets = CommandTarget.None,
            Args = "<client filename>[:<server filename>]",
            Description = """
                Transfers the specified file from the client to the server.
                The server filename, if specified, will be considered relative to the
                current working directory. If omitted, the uploaded file will be placed
                in the current working directory and named the same as on the client.
                """)]
        [CommandHelp(
            Command = "putzip",
            Targets = CommandTarget.None,
            Args = "<client filename>[:<server directory>]",
            Description = """
                Uploads the specified zip file to the server and unzips it in the
                specified directory (considered relative to the current working directory),
                or if none is provided, the working directory itself. This can be used to
                efficiently send a large number of files to the server at once, while
                preserving directory structure.
                """)]
        private void Command_putFileOrZip(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.IsStandard)
            {
                _chat.SendMessage(player, "Your client does not support file transfers.");
                return;
            }

            Span<Range> ranges = stackalloc Range[2];
            int numRanges = parameters.Split(ranges, ':', StringSplitOptions.TrimEntries);
            if (numRanges < 1)
            {
                _chat.SendMessage(player, "Invalid input. Missing client file path.");
                return;
            }

            ReadOnlySpan<char> clientPath = parameters[ranges[0]];
            if (clientPath.IsEmpty)
            {
                _chat.SendMessage(player, "Invalid input. Missing client file path.");
                return;
            }

            ReadOnlySpan<char> clientFileName = Path.GetFileName(clientPath);
            if (clientFileName.IsEmpty)
            {
                _chat.SendMessage(player, "Invalid input. Missing client file name.");
                return;
            }

            bool isZip = command.Equals("putzip", StringComparison.OrdinalIgnoreCase);
            if (isZip && !Path.GetExtension(clientFileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                _chat.SendMessage(player, "Invalid input. File name extension must be '.zip'.");
                return;
            }

            ReadOnlySpan<char> serverPathSpan = [];
            if (numRanges == 2)
            {
                serverPathSpan = parameters[ranges[1]];
            }

            string? workingDir = _fileTransfer.GetWorkingDirectory(player);
            if (workingDir is null)
                return;

            string serverPath = Path.Join(workingDir, serverPathSpan, (!isZip && Path.GetFileName(serverPathSpan).IsEmpty) ? clientFileName : []);
            string serverFullPath = Path.GetFullPath(serverPath);
            string currentDir = Directory.GetCurrentDirectory();

            if (!IsWithinBasePath(serverFullPath, currentDir))
            {
                _chat.SendMessage(player, "Invalid server path.");
                return;
            }

            string serverRelativePath = Path.GetRelativePath(currentDir, serverFullPath);

            char[] clientPathArray = ArrayPool<char>.Shared.Rent(clientPath.Length);
            clientPath.CopyTo(clientPathArray);

            PutFileAsync(player, clientPathArray, clientPath.Length, serverRelativePath, isZip);

            async void PutFileAsync(Player player, char[] clientFileName, int clientFileNameLength, string serverRelativePath, bool isZip)
            {
                try
                {
                    string playerName = player.Name!;
                    Memory<char> clientFileNameMemory = clientFileName.AsMemory(0, clientFileNameLength);

                    string? tempFileName = await _fileTransfer.RequestFileAsync(player, clientFileNameMemory).ConfigureAwait(true);

                    if (tempFileName is null)
                    {
                        if (player == _playerData.FindPlayer(playerName))
                        {
                            _chat.SendMessage(player, $"Error requesting file '{clientFileNameMemory.Span}' to be sent.");
                            return;
                        }

                        return;
                    }

                    await ProcessUploadedFileAsync(
                        tempFileName,
                        new UploadContext(
                            playerName,
                            serverRelativePath,
                            isZip)).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(clientFileName);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<map file>",
            Description = """
                Transfers the specified map file from the client to the server.
                The map will be placed in maps/uploads/<arenabasename>.lvl,
                and the setting General:Map will be changed to the name of the
                uploaded file.
                """)]
        private void Command_putmap(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.IsStandard)
            {
                _chat.SendMessage(player, "Your client does not support file transfers.");
                return;
            }

            if (parameters.IsWhiteSpace())
            {
                _chat.SendMessage(player, "Invalid input. Missing map file name.");
                return;
            }

            if (!Path.GetExtension(parameters).Equals(".lvl", StringComparison.OrdinalIgnoreCase))
            {
                _chat.SendMessage(player, "Invalid input. File name must end with '.lvl'.");
                return;
            }

            Arena? arena = player.Arena;
            if (arena is null)
            {
                _chat.SendMessage(player, "You must be in an arena to use this command.");
                return;
            }

            if (!Directory.Exists(MapUploadDirectory))
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        Directory.CreateDirectory(MapUploadDirectory);
                    }
                    else
                    {
                        Directory.CreateDirectory(MapUploadDirectory, UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                }
                catch (Exception ex)
                {
                    _chat.SendMessage(player, $"Error creating directory '{MapUploadDirectory}'. {ex.Message}");
                    return;
                }
            }

            char[] clientFileName = ArrayPool<char>.Shared.Rent(parameters.Length);
            parameters.CopyTo(clientFileName);

            PutMapAsync(player, arena, clientFileName, parameters.Length);

            async void PutMapAsync(Player player, Arena arena, char[] clientFileName, int clientFileNameLength)
            {
                try
                {
                    string serverPath = Path.Join(MapUploadDirectory, Path.ChangeExtension(arena.BaseName, ".lvl"));

                    string playerName = player.Name!;

                    string? tempFileName = await _fileTransfer.RequestFileAsync(player, clientFileName.AsMemory(0, clientFileNameLength)).ConfigureAwait(true);
                    if (tempFileName is null)
                        return;

                    await ProcessUploadedFileAsync(
                        tempFileName,
                        new UploadContext(
                            playerName,
                            serverPath,
                            false,
                            "General:Map",
                            arena.Name)).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(clientFileName);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<arena name>",
            Description = """
                Creates a directory for a new arena: 'arenas/<arena name>'.
                The current arena's arena.conf is used as a template.
                The generated arena.conf file is standalone (flattened, such 
                that there are no #include or other preprocessor directives).
                This is so that changing settings in the new arena will only
                affect that arena, since no files will be shared.
                """)]
        private void Command_makearena(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena? arena = player.Arena;
            if (arena is null)
                return;

            if (parameters.IsWhiteSpace()
                || parameters.Length > Constants.MaxArenaNameLength)
            {
                _chat.SendMessage(player, "Invalid arena name.");
                return;
            }

            Span<char> arenaName = stackalloc char[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                if ((parameters[i] == '#' && i != 0)
                    || !char.IsAsciiLetterOrDigit(parameters[i]))
                {
                    _chat.SendMessage(player, "Invalid arena name.");
                    return;
                }

                arenaName[i] = char.ToLower(parameters[i]);
            }

            if (char.IsAsciiDigit(arenaName[^1]))
            {
                _chat.SendMessage(player, "Invalid arena name. Do not include arena number.");
                return;
            }

            MakeArenaAsync(player, arena, arenaName.ToString());

            // async local function since the command handler can't be made async
            async void MakeArenaAsync(Player? player, Arena arena, string arenaName)
            {
                ArgumentNullException.ThrowIfNull(player);

                // Save the player name before any await, so that we can check the player object after the await.
                string playerName = player.Name!;

                //
                // Create the directory if necessary.
                //

                string directoryPath = Path.Join("arenas", arenaName);

                if (!await Task.Factory.StartNew(static (p) => Directory.Exists((string)p!), directoryPath).ConfigureAwait(true))
                {
                    try
                    {
                        await Task.Factory.StartNew(static (p) => Directory.CreateDirectory((string)p!), directoryPath).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        if ((player = _playerData.FindPlayer(playerName)) is null)
                            return;

                        _chat.SendMessage(player, $"Error creating directory '{directoryPath}'. {ex.Message}");
                        _logManager.LogP(LogLevel.Warn, nameof(AdminCommand), player, $"Error creating directory '{directoryPath}'. {ex.Message}");
                        return;
                    }
                }

                //
                // Make sure the arena.conf doesn't already exist.
                //

                string arenaConfPath = Path.Join(directoryPath, "arena.conf");

                if (await Task.Factory.StartNew((p) => File.Exists((string)p!), arenaConfPath).ConfigureAwait(true))
                {
                    if ((player = _playerData.FindPlayer(playerName)) is null)
                        return;

                    _chat.SendMessage(player, $"Arena '{arenaName}' already exists.");
                    return;
                }

                if ((player = _playerData.FindPlayer(playerName)) is null)
                    return;

                if (arena != player.Arena)
                    return; // The player changed arenas since issuing the command.

                //
                // Create the arena.conf by creating a standalone copy from the existing arena.
                //

                bool success = await _configManager.SaveStandaloneCopyAsync(arena.Cfg!, arenaConfPath).ConfigureAwait(true);

                if ((player = _playerData.FindPlayer(playerName)) is null)
                    return;

                if (success)
                {
                    _chat.SendMessage(player, $"Successfully created arena.conf '{arenaConfPath}'.");
                }
                else
                {
                    _chat.SendMessage(player, $"Error creating arena.conf '{arenaConfPath}'.");
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[+/-{seeallposn}] [+/-{seeownposn}]",
            Description = """
                Enables or disables bot-specific features.
                {seeallposn} controls whether the bot gets to see all position packets.
                {seeownposn} controls whether you get your own mirror position packets.
                """)]
        private void Command_botfeature(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            foreach (Range range in parameters.SplitAny(" ,"))
            {
                ReadOnlySpan<char> token = parameters[range].Trim();
                if (token.IsEmpty)
                    continue;
                
                bool isEnabled;
                switch (token[0])
                {
                    case '+': isEnabled = true; break;
                    case '-': isEnabled = false; break;
                    default:
                        _chat.SendMessage(player, $"Bad syntax: {token}");
                        continue;
                }

                token = token[1..];

                if (token.Equals("seeallposn", StringComparison.OrdinalIgnoreCase))
                    player.Flags.SeeAllPositionPackets = isEnabled;
                else if (token.Equals("seeownposn", StringComparison.OrdinalIgnoreCase))
                    player.Flags.SeeOwnPosition = isEnabled;
                else
                    _chat.SendMessage(player, $"Unknown bot feature: {token}");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[<server directory>]",
            Description = """
                Changes working directory for file transfer. Note that the specified path
                must be an absolute path; it is not considered relative to the previous
                working directory. If no arguments are specified, return to the server's
                root directory.
                """)]
        private void Command_cd(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            string parametersStr = parameters.IsWhiteSpace() ? "." : parameters.ToString();
            string fullPath = Path.GetFullPath(parametersStr);
            string currentDir = Directory.GetCurrentDirectory();

            if (!IsWithinBasePath(fullPath, currentDir))
            {
                _chat.SendMessage(player, "Invalid path.");
                return;
            }

            ChangeDirectoryAsync(player, parametersStr);

            async void ChangeDirectoryAsync(Player player, string path)
            {
                if (!await Task.Factory.StartNew((obj) => Directory.Exists((string)obj!), parametersStr).ConfigureAwait(true))
                {
                    _chat.SendMessage(player, "The specified path doesn't exist.");
                }
                else
                {
                    _fileTransfer.SetWorkingDirectory(player, Path.GetRelativePath(currentDir, fullPath));
                    _chat.SendMessage(player, "Changed working directory.");
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = """
                Prints the current server-side working directory.
                A working directory of "." indicates the server's root directory.
                """)]
        private void Command_pwd(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _chat.SendMessage(player, $"Current working directory: {_fileTransfer.GetWorkingDirectory(player)}");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<server path>",
            Description = "Delete a file from the server. Paths are relative to the current working directory.")]
        private void Command_delfile(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            string? workingDir = _fileTransfer.GetWorkingDirectory(player);
            if (workingDir is null)
                return;

            string path = Path.Join(workingDir, parameters);
            string fullPath = Path.GetFullPath(path);
            string currentDir = Directory.GetCurrentDirectory();

            if (!IsWithinBasePath(fullPath, currentDir))
            {
                _chat.SendMessage(player, "Invalid path.");
                return;
            }

            DeleteFileAsync(player, path, fullPath);

            // async local function since the command handler can't be made async
            async void DeleteFileAsync(Player? player, string path, string fullPath)
            {
                ArgumentNullException.ThrowIfNull(player);

                // Save the player name before any await, so that we can check the player object after the await.
                string playerName = player.Name!;

                // File.Delete() doesn't throw an exception if the file doesn't exist. So, check ahead of time.
                if (!await Task.Factory.StartNew((p) => File.Exists((string)p!), fullPath).ConfigureAwait(true))
                {
                    // The player's state could have changed (e.g. disconnect) by the time the await continuation executes. Check that the player is still valid.
                    if ((player = _playerData.FindPlayer(playerName)) is null)
                        return;

                    _chat.SendMessage(player, $"File '{path}' not found.");
                    return;
                }

                try
                {
                    await Task.Factory.StartNew((p) => File.Delete((string)p!), fullPath).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    // The player's state could have changed (e.g. disconnect) by the time the await continuation executes. Check that the player is still valid.
                    if ((player = _playerData.FindPlayer(playerName)) is null)
                        return;

                    _chat.SendMessage(player, $"Error deleting '{path}'. {ex.Message}");
                    return;
                }

                // The player's state could have changed (e.g. disconnect) by the time the await continuation executes. Check that the player is still valid.
                if ((player = _playerData.FindPlayer(playerName)) is null)
                    return;

                _chat.SendMessage(player, "Deleted.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<old filename>:<new filename>",
            Description = "Rename a file on the server. Paths are relative to the current working directory.")]
        private void Command_renfile(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Span<Range> ranges = stackalloc Range[2];
            int numRanges = parameters.Split(ranges, ':', StringSplitOptions.TrimEntries);
            if (numRanges != 2)
            {
                _chat.SendMessage(player, "Bad syntax.");
                return;
            }

            ReadOnlySpan<char> oldFileName = parameters[ranges[0]];
            ReadOnlySpan<char> newFileName = parameters[ranges[1]];
            if (oldFileName.IsEmpty || newFileName.IsEmpty)
            {
                _chat.SendMessage(player, "Bad syntax.");
                return;
            }

            string? workingDir = _fileTransfer.GetWorkingDirectory(player);
            if (workingDir is null)
                return;

            string oldPath = Path.Join(workingDir, oldFileName);
            string newPath = Path.Join(workingDir, newFileName);
            string oldFullPath = Path.GetFullPath(oldPath);
            string newFullPath = Path.GetFullPath(newPath);
            string currentDir = Directory.GetCurrentDirectory();

            if (!IsWithinBasePath(oldFullPath, currentDir))
            {
                _chat.SendMessage(player, "Invalid old path.");
                return;
            }

            if (!IsWithinBasePath(newFullPath, currentDir))
            {
                _chat.SendMessage(player, "Invalid new path.");
                return;
            }

            RenameFileAsync(player, oldPath, oldFullPath, newPath, newFullPath);

            // async local function since the command handler can't be made async
            async void RenameFileAsync(Player? player, string oldPath, string oldFullPath, string newPath, string newFullPath)
            {
                ArgumentNullException.ThrowIfNull(player);

                // Save the player name before any await, so that we can check the player object after the await.
                string playerName = player.Name!;

                try
                {
                    await Task.Run(() => File.Move(oldFullPath, newFullPath, true)).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    // The player's state could have changed (e.g. disconnect) by the time the await continuation executes. Check that the player is still valid.
                    if ((player = _playerData.FindPlayer(playerName)) is null)
                        return;

                    _chat.SendMessage(player, $"Error renaming \"{oldPath}\" to \"{newPath}\". {ex.Message}");
                    return;
                }

                // The player's state could have changed (e.g. disconnect) by the time the await continuation executes. Check that the player is still valid.
                if ((player = _playerData.FindPlayer(playerName)) is null)
                    return;

                _chat.SendMessage(player, "Renamed.");
            }
        }


        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = null,
            Description = "Toggles UFO on a player.")]
        private void Command_ufo(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
                targetPlayer = player;

            Span<byte> ufoPacket = [
                (byte)S2CPacketType.Ufo,
                (byte)((targetPlayer.Position.Status & PlayerPositionStatus.Ufo) != 0 ? 0 : 1),
            ];

            _network.SendToOne(targetPlayer, ufoPacket, NetSendFlags.Reliable);
            _chat.SendMessage(player, $"UFO set to {ufoPacket[1]} for {targetPlayer.Name}");
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = null,
            Description = """
                Sends super ship settings to you, or to the target player. Only that player's
                client gets them: weapons and items cost no energy, weapons fire faster, ships
                have more thrust, speed and recharge and start fully upgraded, item limits are
                raised, and bullet, bomb and burst damage levels are 0. The client also fills
                the current ship with energy, items and the top gun and bomb levels.
                It ends when the player's ship is reset (death, ship or freq change, ship reset),
                or when the player is next sent settings, such as on entering an arena or when
                the arena's settings change. Use ?super again to get it back.
                """)]
        private void Command_super(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
                targetPlayer = player;

            // Clients only fill the ship once, when the settings arrive, but keep the settings when the ship is reset.
            // So the Game module sends the usual settings again whenever the ship is reset (see IClientSettings.RestoreClientSettings),
            // rather than leave a ship with no damage and the boosted speed but without the rest of the loadout.
            _clientSettings.SendClientSettings(targetPlayer, MakeSuperSettings);
        }

        #endregion

        #region Super settings

        private static readonly int ShipBulletFireEnergy = ShipShortIndex("BulletFireEnergy");
        private static readonly int ShipMultiFireEnergy = ShipShortIndex("MultiFireEnergy");
        private static readonly int ShipBombFireEnergy = ShipShortIndex("BombFireEnergy");
        private static readonly int ShipBombFireEnergyUpgrade = ShipShortIndex("BombFireEnergyUpgrade");
        private static readonly int ShipLandmineFireEnergy = ShipShortIndex("LandmineFireEnergy");
        private static readonly int ShipLandmineFireEnergyUpgrade = ShipShortIndex("LandmineFireEnergyUpgrade");
        private static readonly int ShipCloakEnergy = ShipShortIndex("CloakEnergy");
        private static readonly int ShipStealthEnergy = ShipShortIndex("StealthEnergy");
        private static readonly int ShipAntiWarpEnergy = ShipShortIndex("AntiWarpEnergy");
        private static readonly int ShipXRadarEnergy = ShipShortIndex("XRadarEnergy");
        private static readonly int ShipAfterburnerEnergy = ShipShortIndex("AfterburnerEnergy");
        private static readonly int ShipBombThrust = ShipShortIndex("BombThrust");
        private static readonly int ShipAttachBounty = ShipShortIndex("AttachBounty");
        private static readonly int ShipBulletFireDelay = ShipShortIndex("BulletFireDelay");
        private static readonly int ShipMultiFireDelay = ShipShortIndex("MultiFireDelay");
        private static readonly int ShipBombFireDelay = ShipShortIndex("BombFireDelay");
        private static readonly int ShipLandmineFireDelay = ShipShortIndex("LandmineFireDelay");
        private static readonly int ShipMaximumRotation = ShipShortIndex("MaximumRotation");
        private static readonly int ShipMaximumThrust = ShipShortIndex("MaximumThrust");
        private static readonly int ShipMaximumSpeed = ShipShortIndex("MaximumSpeed");
        private static readonly int ShipMaximumRecharge = ShipShortIndex("MaximumRecharge");
        private static readonly int ShipMaximumEnergy = ShipShortIndex("MaximumEnergy");
        private static readonly int ShipInitialRotation = ShipShortIndex("InitialRotation");
        private static readonly int ShipInitialThrust = ShipShortIndex("InitialThrust");
        private static readonly int ShipInitialSpeed = ShipShortIndex("InitialSpeed");
        private static readonly int ShipInitialRecharge = ShipShortIndex("InitialRecharge");
        private static readonly int ShipInitialEnergy = ShipShortIndex("InitialEnergy");
        private static readonly int ShipDamageFactor = ShipShortIndex("DamageFactor");
        private static readonly int ShipPrizeShareLimit = ShipShortIndex("PrizeShareLimit");

        private static readonly int ShipMaxMines = ShipByteIndex("MaxMines");
        private static readonly int ShipRepelMax = ShipByteIndex("RepelMax");
        private static readonly int ShipBurstMax = ShipByteIndex("BurstMax");
        private static readonly int ShipDecoyMax = ShipByteIndex("DecoyMax");
        private static readonly int ShipThorMax = ShipByteIndex("ThorMax");
        private static readonly int ShipBrickMax = ShipByteIndex("BrickMax");
        private static readonly int ShipRocketMax = ShipByteIndex("RocketMax");
        private static readonly int ShipPortalMax = ShipByteIndex("PortalMax");

        private static readonly int BulletDamageLevel = Index(ClientSettingsConfig.LongNames, "Bullet", "BulletDamageLevel");
        private static readonly int BulletDamageUpgrade = Index(ClientSettingsConfig.LongNames, "Bullet", "BulletDamageUpgrade");
        private static readonly int BombDamageLevel = Index(ClientSettingsConfig.LongNames, "Bomb", "BombDamageLevel");
        private static readonly int BurstDamageLevel = Index(ClientSettingsConfig.LongNames, "Burst", "BurstDamageLevel");
        private static readonly int EngineShutdownTime = Index(ClientSettingsConfig.ShortNames, "Prize", "EngineShutdownTime");
        private static readonly int MaxPerTeam = Index(ClientSettingsConfig.ByteNames, "Team", "MaxPerTeam");
        private static readonly int TeamMaxMines = Index(ClientSettingsConfig.ByteNames, "Mine", "TeamMaxMines");

        /// <summary>
        /// Changes client settings into the super settings that ?super sends.
        /// </summary>
        /// <param name="settings">The settings to change.</param>
        internal static void MakeSuperSettings(ref S2C_ClientSettings settings)
        {
            for (int i = 0; i < 8; i++)
            {
                ref ShipSettings ship = ref settings.Ships[i];

                // Weapons and items cost no energy.
                ship.Int16Settings[ShipBulletFireEnergy] = 0;
                ship.Int16Settings[ShipMultiFireEnergy] = 0;
                ship.Int16Settings[ShipBombFireEnergy] = 0;
                ship.Int16Settings[ShipBombFireEnergyUpgrade] = 0;
                ship.Int16Settings[ShipLandmineFireEnergy] = 0;
                ship.Int16Settings[ShipLandmineFireEnergyUpgrade] = 0;
                ship.Int16Settings[ShipCloakEnergy] = 0;
                ship.Int16Settings[ShipStealthEnergy] = 0;
                ship.Int16Settings[ShipAntiWarpEnergy] = 0;
                ship.Int16Settings[ShipXRadarEnergy] = 0;
                ship.Int16Settings[ShipAfterburnerEnergy] = 0;
                ship.Int16Settings[ShipBombThrust] = 0;
                ship.Int16Settings[ShipAttachBounty] = 0;

                // Weapons fire faster.
                ship.Int16Settings[ShipBulletFireDelay] = 10;
                ship.Int16Settings[ShipMultiFireDelay] = 10;
                ship.Int16Settings[ShipBombFireDelay] = 10;
                ship.Int16Settings[ShipLandmineFireDelay] = 10;

                // More thrust, speed and recharge, and the ship starts at its maximums.
                // The arithmetic is 16-bit, so a large value wraps.
                unchecked
                {
                    ship.Int16Settings[ShipMaximumThrust] = (short)(ship.Int16Settings[ShipMaximumThrust] * 5 / 2);
                    ship.Int16Settings[ShipMaximumSpeed] = (short)(ship.Int16Settings[ShipMaximumSpeed] * 3);
                    ship.Int16Settings[ShipMaximumRecharge] = (short)(ship.Int16Settings[ShipMaximumRecharge] << 4);
                }

                ship.Int16Settings[ShipInitialRotation] = ship.Int16Settings[ShipMaximumRotation];
                ship.Int16Settings[ShipInitialThrust] = ship.Int16Settings[ShipMaximumThrust];
                ship.Int16Settings[ShipInitialSpeed] = ship.Int16Settings[ShipMaximumSpeed];
                ship.Int16Settings[ShipInitialRecharge] = ship.Int16Settings[ShipMaximumRecharge];
                ship.Int16Settings[ShipInitialEnergy] = ship.Int16Settings[ShipMaximumEnergy];

                // Higher item limits.
                ship.ByteSettings[ShipMaxMines] = 128;
                ship.ByteSettings[ShipRepelMax] = 117;
                ship.ByteSettings[ShipBurstMax] = 128;
                ship.ByteSettings[ShipDecoyMax] = 128;
                ship.ByteSettings[ShipThorMax] = 128;
                ship.ByteSettings[ShipBrickMax] = 128;
                ship.ByteSettings[ShipRocketMax] = 128;
                ship.ByteSettings[ShipPortalMax] = 128;

                ship.Int16Settings[ShipDamageFactor] = 100;
                ship.Int16Settings[ShipPrizeShareLimit] = 999;
                ship.Weapons.ShrapnelRate = 16;
            }

            settings.Int32Settings[BulletDamageLevel] = 0;
            settings.Int32Settings[BulletDamageUpgrade] = 0;
            settings.Int32Settings[BombDamageLevel] = 0;
            settings.Int32Settings[BurstDamageLevel] = 0;
            settings.Int16Settings[EngineShutdownTime] = 0;
            settings.ByteSettings[MaxPerTeam] = 128;
            settings.ByteSettings[TeamMaxMines] = 128;
        }

        private static int ShipShortIndex(string key) => Index(ClientSettingsConfig.ShipShortNames, key);

        private static int ShipByteIndex(string key) => Index(ClientSettingsConfig.ShipByteNames, key);

        private static int Index(string[] keys, string key)
        {
            int index = Array.IndexOf(keys, key);
            return index >= 0 ? index : throw new InvalidOperationException($"Unknown client setting '{key}'.");
        }

        private static int Index((string Section, string Key)[] names, string section, string key)
        {
            int index = Array.IndexOf(names, (section, key));
            return index >= 0 ? index : throw new InvalidOperationException($"Unknown client setting '{section}:{key}'.");
        }

        #endregion

        // This executes on the mainloop thread.
        private async Task ProcessUploadedFileAsync(string? filename, UploadContext context)
        {
            if (string.IsNullOrWhiteSpace(filename))
            {
                // Upload failed or cancelled. There's no file to process so we're done.
                return;
            }

            if (context.Unzip)
            {
                // Do it asynchronously on a worker thread.
                // This complicates matters in that we have to pass info to the thread.
                // We don't want to pass the Player object since the Player could could leave before it's used,
                // and object/id possibly even have been reused by the time we access it, though extremely unlikely.
                _mainloop.QueueThreadPoolWorkItem(ExtractZip, new ExtractZipContext(filename, context.ServerPath, context.PlayerName));
            }
            else
            {
                Player? player;

                try
                {
                    await Task.Run(() => File.Move(filename, context.ServerPath, true)).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(AdminCommand),
                        $"Couldn't move file '{filename}' to '{context.ServerPath}'. {ex.Message}");

                    player = _playerData.FindPlayer(context.PlayerName);
                    if (player is not null)
                    {
                        _chat.SendMessage(player, $"Couldn't upload file to '{context.ServerPath}'.");
                    }

                    try
                    {
                        await Task.Factory.StartNew(static (p) => File.Delete((string)p!), filename).ConfigureAwait(true);
                    }
                    catch
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(AdminCommand), $"Couldn't delete file '{filename}'. {ex.Message}");
                    }

                    return;
                }

                player = _playerData.FindPlayer(context.PlayerName);
                if (player is not null)
                {
                    _chat.SendMessage(player, $"File received: {context.ServerPath}");
                }

                ChangeConfigSetting(context);
            }

            void ExtractZip(ExtractZipContext extractZipContext)
            {
                try
                {
                    ZipFile.ExtractToDirectory(extractZipContext.FileName, extractZipContext.ServerPath, true);
                }
                catch (Exception ex)
                {
                    _playerData.Lock();
                    try
                    {
                        Player? player = _playerData.FindPlayer(extractZipContext.PlayerName);
                        if (player is not null)
                        {
                            _logManager.LogP(LogLevel.Warn, nameof(AdminCommand), player,
                                $"Couldn't extract zip '{extractZipContext.FileName}' to '{extractZipContext.ServerPath}'. {ex.Message}");

                            _chat.SendMessage(player, $"Error extracting zip to \"{extractZipContext.ServerPath}\". {ex.Message}");
                        }
                        else
                        {
                            _logManager.LogM(LogLevel.Warn, nameof(AdminCommand),
                                $"Couldn't extract zip '{extractZipContext.FileName}' to '{extractZipContext.ServerPath}'. {ex.Message}");
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }

                    return;
                }
                finally
                {
                    try
                    {
                        File.Delete(extractZipContext.FileName);
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(AdminCommand), $"Couldn't delete zip '{extractZipContext.FileName}'. {ex.Message}");
                    }
                }

                _playerData.Lock();
                try
                {
                    Player? player = _playerData.FindPlayer(extractZipContext.PlayerName);
                    if (player is not null)
                    {
                        _chat.SendMessage(player, $"Unzipped to \"{extractZipContext.ServerPath}\".");
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }

            void ChangeConfigSetting(UploadContext context)
            {
                ReadOnlySpan<char> setting = context.Setting;
                if (setting.IsWhiteSpace())
                    return;

                _arenaManager.Lock();

                try
                {
                    Arena? arena = _arenaManager.FindArena(context.ArenaName);
                    if (arena is null)
                        return;

                    ConfigHandle? ch = arena.Cfg;
                    if (ch is null)
                        return;

                    Span<Range> ranges = stackalloc Range[2];
                    if (setting.Split(ranges, ':', StringSplitOptions.TrimEntries) != 2)
                        return;

                    ReadOnlySpan<char> section = setting[ranges[0]];
                    ReadOnlySpan<char> key = setting[ranges[1]];
                    if (section.IsEmpty || key.IsEmpty)
                        return;

                    _configManager.SetStr(ch, section.ToString(), key.ToString(), context.ServerPath, $"Set by {context.PlayerName} on {DateTime.UtcNow}", true);
                }
                finally
                {
                    _arenaManager.Unlock();
                }
            }
        }

        /// <summary>
        /// Checks if a <paramref name="path"/> is within a <paramref name="basePath"/>.
        /// </summary>
        /// <remarks>
        /// This method makes the following assumptions:
        /// <list type="bullet">
        /// <item>Both paths are absolute paths that have already been normalized.</item>
        /// <item>Case sensitive match.</item>
        /// </list>
        /// </remarks>
        /// <param name="path">The path to check.</param>
        /// <param name="basePath">The base path.</param>
        /// <returns><see langword="true"/> if <paramref name="path"/> is or is within <paramref name="basePath"/>. Otherwise, <see langword="false"/>.</returns>
        /// <exception cref="ArgumentException"><paramref name="basePath"/> was empty.</exception>
        private static bool IsWithinBasePath(ReadOnlySpan<char> path, scoped ReadOnlySpan<char> basePath)
        {
            if (basePath.IsEmpty)
                throw new ArgumentException("Value was empty.", nameof(basePath));

            // Remove any directory separator chars from the end of basePath.
            ReadOnlySpan<char> directorySeparatorChars = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
            basePath = basePath.TrimEnd(directorySeparatorChars);

            while (!path.IsEmpty)
            {
                if (basePath.Equals(path, StringComparison.Ordinal))
                    return true;

                path = Path.GetDirectoryName(path);
            }

            return false;
        }

        #region Helper types

        private readonly struct UploadContext
        {
            public UploadContext(string playerName, string serverPath, bool unzip) : this(playerName, serverPath, unzip, null, null)
            {
            }

            public UploadContext(string playerName, string serverPath, bool unzip, string? setting, string? arenaName)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
                ArgumentException.ThrowIfNullOrWhiteSpace(serverPath);

                if (!string.IsNullOrWhiteSpace(setting) && string.IsNullOrWhiteSpace(arenaName))
                    throw new ArgumentException("An arena is required when a setting is to be changed.", nameof(arenaName));

                PlayerName = playerName;
                ServerPath = serverPath;
                Unzip = unzip;
                Setting = setting;
                ArenaName = arenaName;
            }

            public string PlayerName { get; }
            public string ServerPath { get; }
            public bool Unzip { get; }
            public string? Setting { get; }
            public string? ArenaName { get; }
        }

        private readonly record struct ExtractZipContext(string FileName, string ServerPath, string PlayerName);

        #endregion
    }
}

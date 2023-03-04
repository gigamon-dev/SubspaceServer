using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.IO;
using System.IO.Compression;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that handles commands for administrators.
    /// </summary>
    [CoreModuleInfo]
    public class AdminCommand : IModule
    {
        private const string MapUploadDirectory = "maps/upload";

        private ICapabilityManager _capabilityManager;
        private IChat _chat;
        private IConfigManager _configManager;
        private ICommandManager _commandManager;
        private IFileTransfer _fileTransfer;
        private ILogFile _logFile;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IPlayerData _playerData;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            ICapabilityManager capabilityManager,
            IChat chat,
            IConfigManager configManager,
            ICommandManager commandManager,
            IFileTransfer fileTransfer,
            ILogFile logFile,
            ILogManager logManager,
            IMainloop mainloop,
            IPlayerData playerData)
        {
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _fileTransfer = fileTransfer ?? throw new ArgumentNullException(nameof(fileTransfer));
            _logFile = logFile ?? throw new ArgumentNullException(nameof(logFile));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _commandManager.AddCommand("admlogfile", Command_admlogfile);
            _commandManager.AddCommand("getfile", Command_getfile);
            _commandManager.AddCommand("putfile", Command_putfile);
            _commandManager.AddCommand("putzip", Command_putzip);
            _commandManager.AddCommand("putmap", Command_putmap);
            _commandManager.AddCommand("botfeature", Command_botfeature);
            _commandManager.AddCommand("cd", Command_cd);
            _commandManager.AddCommand("pwd", Command_pwd);
            _commandManager.AddCommand("delfile", Command_delfile);
            _commandManager.AddCommand("renfile", Command_renfile);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            _commandManager.RemoveCommand("admlogfile", Command_admlogfile);
            _commandManager.RemoveCommand("getfile", Command_getfile);
            _commandManager.RemoveCommand("putfile", Command_putfile);
            _commandManager.RemoveCommand("putzip", Command_putzip);
            _commandManager.RemoveCommand("putmap", Command_putmap);
            _commandManager.RemoveCommand("botfeature", Command_botfeature);
            _commandManager.RemoveCommand("cd", Command_cd);
            _commandManager.RemoveCommand("pwd", Command_pwd);
            _commandManager.RemoveCommand("delfile", Command_delfile);
            _commandManager.RemoveCommand("renfile", Command_renfile);

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
                _mainloop.QueueThreadPoolWorkItem<object>(ThreadPoolWorkItem_Flush, null);
                _chat.SendMessage(player, "Flushing log file.");
            }
            else if (parameters.Equals("reopen", StringComparison.OrdinalIgnoreCase))
            {
                // Do it on a worker thread, don't want to block the mainloop thread.
                _mainloop.QueueThreadPoolWorkItem<object>(ThreadPoolWorkItem_Reopen, null);
                _chat.SendMessage(player, "Reopening log file.");
            }

            void ThreadPoolWorkItem_Flush(object dummy)
            {
                _logFile.Flush();
            }

            void ThreadPoolWorkItem_Reopen(object dummy)
            {
                _logFile.Reopen();
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

            string workingDir = _fileTransfer.GetWorkingDirectory(player);
            string path = Path.Join(workingDir, parameters);
            string fullPath = Path.GetFullPath(path);
            string currentDir = Directory.GetCurrentDirectory();

            if (!new Uri(currentDir).IsBaseOf(new Uri(fullPath)))
            {
                _chat.SendMessage(player, "Invalid path.");
            }
            else
            {
                string relativePath = Path.GetRelativePath(currentDir, fullPath);

                if (!_fileTransfer.SendFile(
                    player,
                    relativePath,
                    Path.GetFileName(fullPath),
                    false))
                {
                    _chat.SendMessage(player, $"Error sending '{relativePath}'.");
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<client filename>[:<server filename>]",
            Description = """
                Transfers the specified file from the client to the server.
                The server filename, if specified, will be considered relative to the
                current working directory. If omitted, the uploaded file will be placed
                in the current working directory and named the same as on the client.
                """)]
        private void Command_putfile(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.IsStandard)
            {
                _chat.SendMessage(player, "Your client does not support file transfers.");
                return;
            }

            ReadOnlySpan<char> clientFileName = parameters.GetToken(':', out ReadOnlySpan<char> serverFileName);
            if (clientFileName.IsEmpty)
            {
                _chat.SendMessage(player, "Bad syntax.");
                return;
            }

            serverFileName = serverFileName.TrimStart(':');

            string workingDir = _fileTransfer.GetWorkingDirectory(player);
            string serverPath = Path.Join(workingDir, !serverFileName.IsEmpty ? serverFileName : clientFileName);
            string serverFullPath = Path.GetFullPath(serverPath);
            string currentDir = Directory.GetCurrentDirectory();

            if (!new Uri(currentDir).IsBaseOf(new Uri(serverFullPath)))
            {
                _chat.SendMessage(player, "Invalid server path.");
            }
            else
            {
                string serverRelativePath = Path.GetRelativePath(currentDir, serverFullPath);

                if (!_fileTransfer.RequestFile(
                    player,
                    clientFileName.ToString(),
                    FileUploaded,
                    new UploadContext(
                        player,
                        serverRelativePath,
                        command.Equals("putzip", StringComparison.OrdinalIgnoreCase))))
                {
                    _chat.SendMessage(player, "Error requesting file to be sent.");
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<client filename>[:<server directory>]",
            Description = """
                Uploads the specified zip file to the server and unzips it in the
                specified directory (considered relative to the current working directory),
                or if none is provided, the working directory itself. This can be used to
                efficiently send a large number of files to the server at once, while
                preserving directory structure.
                """)]
        private void Command_putzip(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.IsStandard)
            {
                _chat.SendMessage(player, "Your client does not support file transfers.");
                return;
            }

            ReadOnlySpan<char> clientFileName = parameters.GetToken(':', out ReadOnlySpan<char> serverDir);
            if (clientFileName.IsEmpty)
            {
                _chat.SendMessage(player, "Bad syntax.");
                return;
            }

            serverDir = serverDir.TrimStart(':');

            string workingDir = _fileTransfer.GetWorkingDirectory(player);
            string serverPath = !serverDir.IsEmpty ? Path.Join(workingDir, serverDir) : workingDir;
            string serverFullPath = Path.GetFullPath(serverPath);
            string currentDir = Directory.GetCurrentDirectory();

            if (!new Uri(currentDir).IsBaseOf(new Uri(serverFullPath)))
            {
                _chat.SendMessage(player, "Invalid server path.");
            }
            else
            {
                string serverRelativePath = Path.GetRelativePath(currentDir, serverFullPath);

                if (!_fileTransfer.RequestFile(
                    player,
                    clientFileName.ToString(),
                    FileUploaded,
                    new UploadContext(
                        player,
                        serverRelativePath,
                        command.Equals("putzip", StringComparison.OrdinalIgnoreCase))))
                {
                    _chat.SendMessage(player, "Error requesting file to be sent.");
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
            if (parameters.IsWhiteSpace())
            {
                _chat.SendMessage(player, "Bad syntax.");
                return;
            }

            if (!player.IsStandard)
            {
                _chat.SendMessage(player, "Your client does not support file transfers.");
                return;
            }

            Arena arena = player.Arena;
            if (arena is null)
            {
                _chat.SendMessage(player, "Your must be in an arena to use this command.");
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

            string serverPath = Path.Join(MapUploadDirectory, Path.ChangeExtension(arena.BaseName, ".lvl"));

            _fileTransfer.RequestFile(
                player,
                parameters.ToString(),
                FileUploaded,
                new UploadContext(
                    player,
                    serverPath,
                    false,
                    "General:Map",
                    player.Arena));
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
            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token;
            while ((token = remaining.GetToken(" ,", out remaining)).Length > 0)
            {
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

            if (!new Uri(currentDir).IsBaseOf(new Uri(fullPath)))
            {
                _chat.SendMessage(player, "Invalid path.");
            }
            else if (!Directory.Exists(parametersStr))
            {
                _chat.SendMessage(player, "The specified path doesn't exist.");
            }
            else
            {
                _fileTransfer.SetWorkingDirectory(player, Path.GetRelativePath(currentDir, fullPath));
                _chat.SendMessage(player, "Changed working directory.");
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
            string workingDir = _fileTransfer.GetWorkingDirectory(player);
            string path = Path.Join(workingDir, parameters);
            string fullPath = Path.GetFullPath(path);
            string currentDir = Directory.GetCurrentDirectory();

            if (!new Uri(currentDir).IsBaseOf(new Uri(fullPath)))
            {
                _chat.SendMessage(player, "Invalid path.");
                return;
            }

            // File.Delete() doesn't throw an exception if the file doesn't exist. So, check ahead of time.
            if (!File.Exists(fullPath))
            {
                _chat.SendMessage(player, $"File '{path}' not found.");
                return;
            }

            try
            {
                File.Delete(fullPath);
            }
            catch (Exception ex)
            {
                _chat.SendMessage(player, $"Error deleting '{path}'. {ex.Message}");
                return;
            }

            _chat.SendMessage(player, "Deleted.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<old filename>:<new filename>",
            Description = "Rename a file on the server. Paths are relative to the current working directory.")]
        private void Command_renfile(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ReadOnlySpan<char> oldFileName = parameters.GetToken(':', out ReadOnlySpan<char> newFileName);
            if (oldFileName.IsEmpty
                || (newFileName = newFileName.TrimStart(':')).IsEmpty)
            {
                _chat.SendMessage(player, "Bad syntax.");
                return;
            }

            string workingDir = _fileTransfer.GetWorkingDirectory(player);
            string oldPath = Path.Join(workingDir, oldFileName);
            string newPath = Path.Join(workingDir, newFileName);
            string oldFullPath = Path.GetFullPath(oldPath);
            string newFullPath = Path.GetFullPath(newPath);
            string currentDir = Directory.GetCurrentDirectory();

            if (!new Uri(currentDir).IsBaseOf(new Uri(oldFullPath)))
            {
                _chat.SendMessage(player, "Invalid old path.");
                return;
            }

            if (!new Uri(currentDir).IsBaseOf(new Uri(newFullPath)))
            {
                _chat.SendMessage(player, "Invalid new path.");
                return;
            }

            try
            {
                File.Move(oldFullPath, newFullPath, true);
            }
            catch (Exception ex)
            {
                _chat.SendMessage(player, $"Error renaming \"{oldPath}\" to \"{newPath}\". {ex.Message}");
                return;
            }

            _chat.SendMessage(player, "Renamed.");
        }

        #endregion

        private void FileUploaded(string filename, UploadContext context)
        {
            if (string.IsNullOrWhiteSpace(filename))
                return;

            if (context.Unzip)
            {
                // Do it asynchronously on a worker thread.
                // This complicates matters in that we have to pass info to the thread.
                // We don't want to pass the Player object since the Player could could leave before it's used,
                // and object/id possibly even have been reused by the time we access it, though extremely unlikely.
                _mainloop.QueueThreadPoolWorkItem(ExtractZip, new ExtractZipContext(filename, context.ServerPath, context.Player.Name));
            }
            else
            {
                try
                {
                    // TODO: this should be done on a worker thread
                    File.Move(filename, context.ServerPath, true);
                }
                catch (Exception ex)
                {
                    _logManager.LogP(LogLevel.Warn, nameof(AdminCommand), context.Player,
                        $"Couldn't move file '{filename}' to '{context.ServerPath}'. {ex.Message}");

                    _chat.SendMessage(context.Player, $"Couldn't upload file to '{context.ServerPath}'.");

                    try
                    {
                        File.Delete(filename);
                    }
                    catch
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(AdminCommand), $"Couldn't delete file '{filename}'. {ex.Message}");
                    }

                    return;
                }

                _chat.SendMessage(context.Player, $"File received: {context.ServerPath}");

                ReadOnlySpan<char> setting = context.Setting;
                if (!setting.IsWhiteSpace())
                {
                    if (context.Arena is null || context.Player.Arena != context.Arena)
                    {
                        _chat.SendMessage(context.Player, "Map upload aborted (changed arenas).");
                        return;
                    }

                    ReadOnlySpan<char> section = context.Setting.AsSpan().GetToken(':', out ReadOnlySpan<char> key);
                    if (section.IsEmpty
                        || (key = key.TrimStart(':')).IsEmpty)
                    {
                        return;
                    }

                    _configManager.SetStr(context.Arena.Cfg, section.ToString(), key.ToString(), context.ServerPath, $"Set by {context.Player.Name} on {DateTime.UtcNow}", true);
                }
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
                        Player player = _playerData.FindPlayer(extractZipContext.PlayerName);
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
                    Player player = _playerData.FindPlayer(extractZipContext.PlayerName);
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
        }

        #region Helper types

        private readonly struct UploadContext
        {
            public UploadContext(Player player, string serverPath, bool unzip) : this(player, serverPath, unzip, null, null)
            {
                if (string.IsNullOrWhiteSpace(serverPath))
                    throw new ArgumentException("Cannot be null or white-space.", nameof(serverPath));

                Player = player ?? throw new ArgumentNullException(nameof(player));
                ServerPath = serverPath;
                Unzip = unzip;
            }

            public UploadContext(Player player, string serverPath, bool unzip, string setting, Arena arena)
            {
                if (string.IsNullOrWhiteSpace(serverPath))
                    throw new ArgumentException("Cannot be null or white-space.", nameof(serverPath));

                if (!string.IsNullOrWhiteSpace(setting) && arena is null)
                    throw new ArgumentException("An arena is required when a setting is to be changed.");

                Player = player ?? throw new ArgumentNullException(nameof(player));
                ServerPath = serverPath;
                Unzip = unzip;
                Setting = setting;
                Arena = arena;
            }

            public Player Player { get; }
            public string ServerPath { get; }
            public bool Unzip { get; }
            public string Setting { get; }
            public Arena Arena { get; }
        }

        private readonly record struct ExtractZipContext(string FileName, string ServerPath, string PlayerName);

        #endregion
    }
}

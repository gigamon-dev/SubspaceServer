using SS.Core.ComponentInterfaces;
using System;
using System.IO;

namespace SS.Core.Modules
{
    [CoreModuleInfo]
    public class AdminCommand : IModule
    {
        private ICapabilityManager capabilityManager;
        private IChat chat;
        private ICommandManager commandManager;
        private IFileTransfer fileTransfer;
        private ILogManager logManager;

        public bool Load(
            ComponentBroker broker,
            ICapabilityManager capabilityManager,
            IChat chat,
            ICommandManager commandManager,
            IFileTransfer fileTransfer,
            ILogManager logManager)
        {
            this.capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            this.chat = chat ?? throw new ArgumentNullException(nameof(chat));
            this.commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            this.fileTransfer = fileTransfer ?? throw new ArgumentNullException(nameof(fileTransfer));
            this.logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

            commandManager.AddCommand("getfile", Command_getfile);
            commandManager.AddCommand("putfile", Command_putfile);
            commandManager.AddCommand("cd", Command_cd);
            commandManager.AddCommand("pwd", Command_pwd);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            commandManager.RemoveCommand("getfile", Command_getfile);
            commandManager.RemoveCommand("putfile", Command_putfile);
            commandManager.RemoveCommand("cd", Command_cd);
            commandManager.RemoveCommand("pwd", Command_pwd);

            return true;
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<filename>",
            Description =
                "Transfers the specified file from the server to the client. The filename\n" +
                "is considered relative to the current working directory.")]
        private void Command_getfile(string command, string parameters, Player p, ITarget target)
        {
            if (!p.IsStandard)
            {
                chat.SendMessage(p, "Your client does not support file transfers.");
                return;
            }

            string workingDir = fileTransfer.GetWorkingDirectory(p);
            string path = Path.Combine(workingDir, parameters);

            string fullPath = Path.GetFullPath(path);
            string currentDir = Directory.GetCurrentDirectory();

            if (!new Uri(currentDir).IsBaseOf(new Uri(fullPath)))
            {
                chat.SendMessage(p, "Invalid path.");
            }
            else
            {
                string relativePath = Path.GetRelativePath(currentDir, fullPath);

                if (!fileTransfer.SendFile(
                    p,
                    relativePath,
                    Path.GetFileName(fullPath),
                    false))
                {
                    chat.SendMessage(p, "Error sending '{0}'.", relativePath);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<client filename>[:<server filename>]",
            Description =
                "Transfers the specified file from the client to the server.\n" +
                "The server filename, if specified, will be considered relative to the\n" +
                "current working directory. If omitted, the uploaded file will be placed\n" +
                "in the current working directory and named the same as on the client.")]
        private void Command_putfile(string command, string parameters, Player p, ITarget target)
        {
            if (!p.IsStandard)
            {
                chat.SendMessage(p, "Your client does not support file transfers.");
                return;
            }

            string workingDir = fileTransfer.GetWorkingDirectory(p);
            string clientFilename;
            string serverFilename;

            int colonIndex = parameters.IndexOf(':');
            if (colonIndex != -1)
            {
                clientFilename = parameters.Substring(0, colonIndex);
                serverFilename = (colonIndex == parameters.Length - 1)
                    ? null
                    : parameters.Substring(colonIndex + 1);
            }
            else
            {
                clientFilename = parameters;
                serverFilename = null;
            }

            string serverPath = Path.Combine(workingDir, serverFilename ?? Path.GetFileName(clientFilename));
            string fullPath = Path.GetFullPath(serverPath);
            string currentDir = Directory.GetCurrentDirectory();

            if (!new Uri(currentDir).IsBaseOf(new Uri(fullPath)))
            {
                chat.SendMessage(p, "Invalid server path.");
            }
            else
            {
                fileTransfer.RequestFile(
                    p, 
                    clientFilename, 
                    FileUploaded,
                    new UploadContext(
                        p,
                        Path.GetRelativePath(currentDir, fullPath),
                        false));
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[<server directory>]",
            Description =
                "Changes working directory for file transfer. Note that the specified path\n" +
                "must be an absolute path; it is not considered relative to the previous\n" +
                "working directory. If no arguments are specified, return to the server's\n" +
                "root directory.")]
        private void Command_cd(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                parameters = ".";

            string fullPath = Path.GetFullPath(parameters);
            string currentDir = Directory.GetCurrentDirectory();

            if (!new Uri(currentDir).IsBaseOf(new Uri(fullPath)))
            {
                chat.SendMessage(p, "Invalid path.");
            }
            else if (!Directory.Exists(parameters))
            {
                chat.SendMessage(p, "The specified path doesn't exist.");
            }
            else
            {
                fileTransfer.SetWorkingDirectory(p, Path.GetRelativePath(currentDir, fullPath));
                chat.SendMessage(p, "Changed working directory.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None, 
            Args = null,
            Description = 
                "Prints the current server-side working directory.\n" +
                "A working directory of \".\" indicates the server's root directory.")]
        private void Command_pwd(string command, string parameters, Player p, ITarget target)
        {
            chat.SendMessage(p, "Current working directory: {0}", fileTransfer.GetWorkingDirectory(p));
        }

        private void FileUploaded(string filename, UploadContext context)
        {
            if (string.IsNullOrWhiteSpace(filename))
                return;

            if (context.Unzip)
            {
            }
            else
            {
                try
                {
                    File.Move(filename, context.ServerPath);
                }
                catch (Exception ex)
                {
                    logManager.LogP(LogLevel.Warn, nameof(AdminCommand), context.Player,
                        "Couldn't move file '{0}' to '{1}'. {2}", filename, context.ServerPath, ex.Message);

                    chat.SendMessage(context.Player, "Couldn't upload file to '{0}'", context.ServerPath);

                    try
                    {
                        File.Delete(filename);
                    }
                    catch
                    {
                    }

                    return;
                }

                chat.SendMessage(context.Player, "File received: {0}", context.ServerPath);
            }   
        }

        private class UploadContext
        {
            public UploadContext(Player player, string serverPath, bool unzip)
            {
                if (string.IsNullOrWhiteSpace(serverPath))
                    throw new ArgumentException("Cannot be null or white-space.", nameof(serverPath));

                Player = player ?? throw new ArgumentNullException(nameof(player));
                ServerPath = serverPath;
                Unzip = unzip;
            }

            public Player Player { get; }
            public string ServerPath { get; }
            public bool Unzip { get; }
        }
    }
}

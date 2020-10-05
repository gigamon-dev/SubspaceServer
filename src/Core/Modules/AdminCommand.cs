using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Text;

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


            commandManager.AddCommand("putfile", Command_putfile);
            commandManager.AddCommand("pwd", Command_pwd);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            commandManager.RemoveCommand("pwd", Command_pwd);

            return true;
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

            // TODO: implement this fully, for now just for testing

            UploadContext uploadContext = new UploadContext(p);
            fileTransfer.RequestFile(p, parameters, FileUploaded, uploadContext);
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
            chat.SendMessage(context.Player, "File received: '{0}'", filename);
        }

        private class UploadContext
        {
            public Player Player;
            public bool Unzip = false;

            public UploadContext(Player p)
            {
                Player = p;
            }
        }
    }
}

using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    public class Quickfix : IModule
    {
        private ICommandManager commandManager;
        private ICapabilityManager capabilityManager;
        private IChat chat;
        private IConfigHelp configHelp;
        private IConfigManager configManager;
        private IFileTransfer fileTransfer;
        private ILogManager log;
        private INetwork net;

        public bool Load(
            ComponentBroker broker,
            ICapabilityManager capabilityManager,
            ICommandManager commandManager,
            IChat chat,
            IConfigHelp configHelp,
            IConfigManager configManager,
            IFileTransfer fileTransfer,
            ILogManager log,
            INetwork net)
        {
            this.capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            this.commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            this.chat = chat ?? throw new ArgumentNullException(nameof(chat));
            this.configHelp = configHelp ?? throw new ArgumentNullException(nameof(configHelp));
            this.configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            this.fileTransfer = fileTransfer ?? throw new ArgumentNullException(nameof(fileTransfer));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.net = net ?? throw new ArgumentNullException(nameof(net));

            net.AddPacket(Packets.C2SPacketType.SettingChange, Packet_SettingChange);
            commandManager.AddCommand("quickfix", Command_quickfix);
            commandManager.AddCommand("getsettings", Command_quickfix);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            net.RemovePacket(Packets.C2SPacketType.SettingChange, Packet_SettingChange);
            commandManager.RemoveCommand("quickfix", Command_quickfix);
            commandManager.RemoveCommand("getsettings", Command_quickfix);
            return true;
        }

        private void Packet_SettingChange(Player p, byte[] data, int length)
        {
            
        }

        private void Command_quickfix(string command, string parameters, Player p, ITarget target)
        {
            if (!capabilityManager.HasCapability(p, Constants.Capabilities.ChangeSettings))
            {
                chat.SendMessage(p, "You are not authorized to view or change settings in this arena.");
                return;
            }

            ConfigHandle arenaConfigHandle = p.Arena?.Cfg;
            if (arenaConfigHandle == null)
                return;

            // TODO: decide on whether to use
            //Path.GetTempFileName();
            string filename = Path.Combine("tmp", $"server-{Guid.NewGuid().ToString("N")}.set");
            try
            {

                using FileStream fs = File.Open(filename, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using StreamWriter writer = new StreamWriter(fs, StringUtils.DefaultEncoding);

                foreach (var sectionGrouping in configHelp.Sections.OrderBy(s => s.Key))
                {
                    if (string.Equals(sectionGrouping.Key, "All", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] shipNames = Enum.GetNames<ShipType>();

                        for (int i = 0; i < (int)ShipType.Spec; i++)
                        {
                            TryWriteSection(parameters, sectionGrouping, arenaConfigHandle, writer, shipNames[i]);
                        }
                    }
                    else
                    {
                        TryWriteSection(parameters, sectionGrouping, arenaConfigHandle, writer, sectionGrouping.Key);
                    }
                }
            }
            catch
            {
            }
            
            //fileTransfer.SendFile(p, )
        }

        private void TryWriteSection(
            string filter, 
            IGrouping<string, (ConfigHelpAttribute Attr, string ModuleTypeName)> sectionGrouping, 
            ConfigHandle configHandle, 
            StreamWriter writer, 
            string sectionName)
        {
            bool sectionMatch = string.IsNullOrWhiteSpace(filter) || sectionName.Contains(filter, StringComparison.OrdinalIgnoreCase);

            foreach ((ConfigHelpAttribute attribute, string moduleTypeName) in sectionGrouping.OrderBy(item => item.Attr.Key))
            {
                bool keyMatch = string.IsNullOrWhiteSpace(filter) || attribute.Key.Contains(filter, StringComparison.OrdinalIgnoreCase);

                if ((sectionMatch || keyMatch)
                    && attribute.Scope == ConfigScope.Arena
                    && string.IsNullOrWhiteSpace(attribute.FileName))
                {
                    string value = configManager.GetStr(configHandle, sectionName, attribute.Key);
                    if (value == null)
                        value = "<unset>";

                    if (!string.IsNullOrWhiteSpace(attribute.Range))
                    {
                        int dashIndex = attribute.Range.IndexOf('-');
                        if (int.TryParse(attribute.Range[0..(dashIndex - 1)], out int min)
                            && int.TryParse(attribute.Range[(dashIndex + 1)..], out int max))
                        {
                            writer.Write($"{sectionName}:{attribute.Key}:{value}:{min}:{max}:{attribute.Description}\r\n");
                            continue;
                        }
                    }

                    writer.Write($"{sectionName}:{attribute.Key}:{value}:::{attribute.Description}\r\n");
                }
            }
        }
    }
}

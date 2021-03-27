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
    /// <summary>
    /// Module that provides functionality that clients use to display an in-game user interface for managing arena settings.
    /// This includes:
    /// <list type="bullet">
    /// <item>The ?quickfix command (AKA, ?getsettings for subgame compatibility) to download arena settings in a text based delimited file format.</item>
    /// <item>A packet handler (<see cref="Packets.C2SPacketType.SettingChange"/>) for processing requests from clients to update settings.</item>
    /// </list>
    /// </summary>
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
            if (!capabilityManager.HasCapability(p, Constants.Capabilities.ChangeSettings))
            {
                chat.SendMessage(p, "You are not authorized to view or change settings in this arena.");
                return;
            }

            ConfigHandle arenaConfigHandle = p.Arena?.Cfg;
            if (arenaConfigHandle == null)
                return;

            string comment = $"Set by {p.Name} with ?quickfix on {DateTime.UtcNow}";
            bool permanent = true;
            int position = 1;

            while (position < length)
            {
                int nullIndex = Array.IndexOf<byte>(data, 0, position);
                if (nullIndex == -1 || nullIndex == position)
                    break;
                
                if (nullIndex > position)
                {
                    string delimitedSetting = StringUtils.ReadNullTerminatedString(data.AsSpan(position, nullIndex - position));
                    string[] tokens = delimitedSetting.Split(':', 3, StringSplitOptions.None);

                    if (tokens.Length != 3)
                    {
                        log.LogP(LogLevel.Malicious, nameof(Quickfix), p, "Badly formatted setting change.");
                        return;
                    }

                    if (string.Equals(tokens[0], "__pragma", StringComparison.Ordinal))
                    {
                        if (string.Equals(tokens[1], "perm", StringComparison.Ordinal))
                        {
                            // send __pragma:perm:0 to make further settings changes temporary
                            permanent = tokens[2] != "0";
                        }
                        else if (string.Equals(tokens[1], "flush", StringComparison.Ordinal)
                            && tokens[2] == "1")
                        {
                            // send __pragma:flush:1 to flush settings changes
                            //TODO: configManager.FlushDirtyValues();
                        }
                    }
                    else
                    {
                        log.LogP(LogLevel.Info, nameof(Quickfix), p, $"setting {tokens[0]}:{tokens[1]} = {tokens[2]}");
                        configManager.SetStr(arenaConfigHandle, tokens[0], tokens[1], tokens[2], comment, permanent);
                    }
                }

                position = nullIndex + 1;
            }
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

            // TODO: setting on whether to use the system's temp folder (Path.GetTempFileName()) or use our own tmp folder
            string path = Path.Combine("tmp", $"server-{Guid.NewGuid():N}.set");
            bool hasData = false;

            try
            {

                using FileStream fs = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
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

                hasData = fs.Position > 0;
            }
            catch (Exception ex)
            {
                log.LogM(LogLevel.Warn, nameof(Quickfix), $"Failed to create temporary server.set file '{path}'. {ex.Message}");
                
                if (File.Exists(path))
                {
                    DeleteTempFile(path);
                }
                return;
            }

            if (hasData)
            {
                chat.SendMessage(p, "Sending settings...");
                fileTransfer.SendFile(p, path, "server.set", true);
            }
            else
            {
                chat.SendMessage(p, "No settings matches your query.");
                DeleteTempFile(path);
            }

            void DeleteTempFile(string path)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    log.LogM(LogLevel.Warn, nameof(Quickfix), $"Failed to delete temporary server.set file '{path}'. {ex.Message}");
                }
            }
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

                        if (dashIndex != -1
                            && int.TryParse(attribute.Range[0..dashIndex], out int min)
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

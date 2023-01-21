using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.IO;
using System.Linq;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality that clients use to display an in-game user interface for managing arena settings.
    /// This includes:
    /// <list type="bullet">
    /// <item>The ?quickfix command (aliased as ?getsettings for subgame compatibility) to download arena settings in a text based delimited file format.</item>
    /// <item>A packet handler (<see cref="Packets.C2SPacketType.SettingChange"/>) for processing requests from clients to update settings.</item>
    /// </list>
    /// </summary>
    [CoreModuleInfo]
    public class Quickfix : IModule
    {
        private ICommandManager _commandManager;
        private ICapabilityManager _capabilityManager;
        private IChat _chat;
        private IConfigHelp _configHelp;
        private IConfigManager _configManager;
        private IFileTransfer _fileTransfer;
        private ILogManager _logManager;
        private INetwork _network;

        public bool Load(
            ComponentBroker broker,
            ICapabilityManager capabilityManager,
            ICommandManager commandManager,
            IChat chat,
            IConfigHelp configHelp,
            IConfigManager configManager,
            IFileTransfer fileTransfer,
            ILogManager logManager,
            INetwork network)
        {
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configHelp = configHelp ?? throw new ArgumentNullException(nameof(configHelp));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _fileTransfer = fileTransfer ?? throw new ArgumentNullException(nameof(fileTransfer));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));

            network.AddPacket(C2SPacketType.SettingChange, Packet_SettingChange);
            commandManager.AddCommand("quickfix", Command_quickfix);
            commandManager.AddCommand("getsettings", Command_quickfix);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            _network.RemovePacket(C2SPacketType.SettingChange, Packet_SettingChange);
            _commandManager.RemoveCommand("quickfix", Command_quickfix);
            _commandManager.RemoveCommand("getsettings", Command_quickfix);
            return true;
        }

        private void Packet_SettingChange(Player player, byte[] data, int length, NetReceiveFlags flags)
        {
            if (!_capabilityManager.HasCapability(player, Constants.Capabilities.ChangeSettings))
            {
                _chat.SendMessage(player, "You are not authorized to view or change settings in this arena.");
                return;
            }

            ConfigHandle arenaConfigHandle = player.Arena?.Cfg;
            if (arenaConfigHandle == null)
                return;

            string comment = $"Set by {player.Name} with ?quickfix on {DateTime.UtcNow}";
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
                        _logManager.LogP(LogLevel.Malicious, nameof(Quickfix), player, "Badly formatted setting change.");
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
                        _logManager.LogP(LogLevel.Info, nameof(Quickfix), player, $"Setting {tokens[0]}:{tokens[1]} = {tokens[2]}");
                        _configManager.SetStr(arenaConfigHandle, tokens[0], tokens[1], tokens[2], comment, permanent);
                    }
                }

                position = nullIndex + 1;
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<limiting text>",
            Description = 
            "Lets you quickly change arena settings. This will display some list of\n" +
            "settings with their current values and allow you to change them. The\n" +
            "argument to this command can be used to limit the list of settings\n" +
            "displayed. (With no arguments, equivalent to ?getsettings in subgame.)")]
        private void Command_quickfix(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!_capabilityManager.HasCapability(player, Constants.Capabilities.ChangeSettings))
            {
                _chat.SendMessage(player, "You are not authorized to view or change settings in this arena.");
                return;
            }

            ConfigHandle arenaConfigHandle = player.Arena?.Cfg;
            if (arenaConfigHandle == null)
                return;

            // TODO: setting on whether to use the system's temp folder (Path.GetTempFileName()) or use our own tmp folder
            string path = Path.Combine("tmp", $"server-{Guid.NewGuid():N}.set");
            bool hasData = false;

            try
            {
                using FileStream fs = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using StreamWriter writer = new(fs, StringUtils.DefaultEncoding);

                foreach (var sectionGrouping in _configHelp.Sections.OrderBy(s => s.Key))
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
                _logManager.LogM(LogLevel.Warn, nameof(Quickfix), $"Failed to create temporary server.set file '{path}'. {ex.Message}");
                
                if (File.Exists(path))
                {
                    DeleteTempFile(path);
                }
                return;
            }

            if (hasData)
            {
                _chat.SendMessage(player, "Sending settings...");
                _fileTransfer.SendFile(player, path, "server.set", true);
            }
            else
            {
                _chat.SendMessage(player, "No settings matches your query.");
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
                    _logManager.LogM(LogLevel.Warn, nameof(Quickfix), $"Failed to delete temporary server.set file '{path}'. {ex.Message}");
                }
            }
        }

        private void TryWriteSection(
            ReadOnlySpan<char> filter, 
            IGrouping<string, (ConfigHelpAttribute Attr, string ModuleTypeName)> sectionGrouping, 
            ConfigHandle configHandle, 
            StreamWriter writer, 
            string sectionName)
        {
            bool sectionMatch = filter.IsWhiteSpace() || sectionName.AsSpan().Contains(filter, StringComparison.OrdinalIgnoreCase);

            foreach ((ConfigHelpAttribute attribute, string moduleTypeName) in sectionGrouping.OrderBy(item => item.Attr.Key))
            {
                bool keyMatch = filter.IsWhiteSpace() || attribute.Key.AsSpan().Contains(filter, StringComparison.OrdinalIgnoreCase);

                if ((sectionMatch || keyMatch)
                    && attribute.Scope == ConfigScope.Arena
                    && string.IsNullOrWhiteSpace(attribute.FileName))
                {
                    string value = _configManager.GetStr(configHandle, sectionName, attribute.Key);
                    value ??= "<unset>";

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

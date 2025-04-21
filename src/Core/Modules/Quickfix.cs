using SS.Core.ComponentInterfaces;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
    public sealed class Quickfix : IModule
    {
        private readonly ICommandManager _commandManager;
        private readonly ICapabilityManager _capabilityManager;
        private readonly IChat _chat;
        private readonly IConfigHelp _configHelp;
        private readonly IConfigManager _configManager;
        private readonly IFileTransfer _fileTransfer;
        private readonly ILogManager _logManager;
        private readonly INetwork _network;
        private readonly IObjectPoolManager _objectPoolManager;

        public Quickfix(
            ICapabilityManager capabilityManager,
            ICommandManager commandManager,
            IChat chat,
            IConfigHelp configHelp,
            IConfigManager configManager,
            IFileTransfer fileTransfer,
            ILogManager logManager,
            INetwork network,
            IObjectPoolManager objectPoolManager)
        {
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configHelp = configHelp ?? throw new ArgumentNullException(nameof(configHelp));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _fileTransfer = fileTransfer ?? throw new ArgumentNullException(nameof(fileTransfer));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        }

        bool IModule.Load(IComponentBroker broker)
        {
            _network.AddPacket(C2SPacketType.SettingChange, Packet_SettingChange);
            _commandManager.AddCommand("quickfix", Command_quickfix);
            _commandManager.AddCommand("getsettings", Command_quickfix);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            _network.RemovePacket(C2SPacketType.SettingChange, Packet_SettingChange);
            _commandManager.RemoveCommand("quickfix", Command_quickfix);
            _commandManager.RemoveCommand("getsettings", Command_quickfix);
            return true;
        }

        private void Packet_SettingChange(Player player, ReadOnlySpan<byte> data, NetReceiveFlags flags)
        {
            if (!_capabilityManager.HasCapability(player, Constants.Capabilities.ChangeSettings))
            {
                _chat.SendMessage(player, "You are not authorized to view or change settings in this arena.");
                return;
            }

            ConfigHandle? arenaConfigHandle = player.Arena?.Cfg;
            if (arenaConfigHandle is null)
                return;

            string comment = $"Set by {player.Name} with ?quickfix on {DateTime.UtcNow}";
            bool permanent = true;
            data = data[1..];
            while (!data.IsEmpty)
            {
                int nullIndex = data.IndexOf((byte)0);
                if (nullIndex == -1 || nullIndex == 0)
                    break;

                ReadOnlySpan<byte> strBytes = data[..nullIndex];
                if (!ProcessOneChange(player, strBytes, ref permanent))
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(Quickfix), player, "Badly formatted setting change.");
                    return;
                }

                data = data[(nullIndex + 1)..];
            }

            bool ProcessOneChange(Player player, ReadOnlySpan<byte> strBytes, ref bool permanent)
            {
                char[]? buffer = null;
                try
                {
                    int charCount = StringUtils.DefaultEncoding.GetCharCount(strBytes);
                    Span<char> delimitedString = charCount > 1024 ? (buffer = ArrayPool<char>.Shared.Rent(charCount)).AsSpan(0, charCount) : stackalloc char[charCount];
                    if (StringUtils.DefaultEncoding.GetChars(strBytes, delimitedString) != delimitedString.Length)
                        return false;

                    ReadOnlySpan<char> token1 = delimitedString.GetToken(':', out delimitedString);
                    if (token1.IsEmpty || delimitedString.IsEmpty)
                        return false;

                    ReadOnlySpan<char> token2 = delimitedString.GetToken(':', out delimitedString);
                    if (token2.IsEmpty || delimitedString.IsEmpty)
                        return false;

                    ReadOnlySpan<char> token3 = delimitedString[1..];
                    if (token3.IsEmpty)
                        return false;

                    if (token1.Equals("__pragma", StringComparison.Ordinal))
                    {
                        if (token2.Equals("perm", StringComparison.Ordinal))
                        {
                            // send __pragma:perm:0 to make further settings changes temporary
                            permanent = !token3.Equals("0", StringComparison.Ordinal);
                        }
                        else if (token2.Equals("flush", StringComparison.Ordinal)
                            && token3.Equals("1", StringComparison.Ordinal))
                        {
                            // send __pragma:flush:1 to flush settings changes
                            //TODO: configManager.FlushDirtyValues();
                        }
                    }
                    else
                    {
                        _logManager.LogP(LogLevel.Info, nameof(Quickfix), player, $"Setting {token1}:{token2} = {token3}");
                        _configManager.SetStr(arenaConfigHandle, token1.ToString(), token2.ToString(), token3.ToString(), comment, permanent);
                    }

                    return true;
                }
                finally
                {
                    if (buffer is not null)
                        ArrayPool<char>.Shared.Return(buffer);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<limiting text>",
            Description = """
                Lets you quickly change arena settings. This will display some list of
                settings with their current values and allow you to change them. The
                argument to this command can be used to limit the list of settings
                displayed. (With no arguments, equivalent to ?getsettings in subgame.)
                """)]
        private void Command_quickfix(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!_capabilityManager.HasCapability(player, Constants.Capabilities.ChangeSettings))
            {
                _chat.SendMessage(player, "You are not authorized to view or change settings in this arena.");
                return;
            }

            ConfigHandle? arenaConfigHandle = player.Arena?.Cfg;
            if (arenaConfigHandle is null)
            {
                _chat.SendMessage(player, "You must be in an arena to view or change settings.");
                return;
            }

            bool hasData = false;
            MemoryStream stream = new(); // TODO: Use Microsoft.IO.RecyclableMemoryStream instead

            try
            {
                using StreamWriter writer = new(stream, StringUtils.DefaultEncoding, leaveOpen: true);

                _configHelp.Lock();

                try
                {
                    foreach (string section in _configHelp.Sections)
                    {
                        if (string.Equals(section, "All", StringComparison.OrdinalIgnoreCase))
                        {
                            string[] shipNames = Enum.GetNames<ShipType>();

                            for (int i = 0; i < (int)ShipType.Spec; i++)
                            {
                                TryWriteSection(parameters, section, arenaConfigHandle, writer, shipNames[i]);
                            }
                        }
                        else
                        {
                            TryWriteSection(parameters, section, arenaConfigHandle, writer, section);
                        }
                    }
                }
                finally
                {
                    _configHelp.Unlock();
                }

                writer.Flush();
                hasData = stream.Position > 0;
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Warn, nameof(Quickfix), $"Failed to server.set file. {ex.Message}");
                stream.Dispose();
                return;
            }

            if (!hasData)
            {
                _chat.SendMessage(player, "No settings matched your query.");
                stream.Dispose();
                return;
            }

            stream.Position = 0;

            _chat.SendMessage(player, "Sending settings...");

            if (!_fileTransfer.SendFile(player, stream, "server.set"))
            {
                _logManager.LogP(LogLevel.Warn, nameof(Quickfix), player, "Failed to send server.set file.");
            }
        }

        private void TryWriteSection(
            ReadOnlySpan<char> filter,
            string helpSection,
            ConfigHandle configHandle,
            StreamWriter writer,
            string sectionName)
        {
            bool sectionMatch = filter.IsWhiteSpace() || sectionName.AsSpan().Contains(filter, StringComparison.OrdinalIgnoreCase);

            if (!_configHelp.TryGetSectionKeys(helpSection, out IReadOnlyList<string>? keyList))
                return;

            for (int keyIndex = 0; keyIndex < keyList.Count; keyIndex++)
            {
                string key = keyList[keyIndex];
                bool keyMatch = filter.IsWhiteSpace() || key.AsSpan().Contains(filter, StringComparison.OrdinalIgnoreCase);

                if (!sectionMatch && !keyMatch)
                    continue;

                if (!_configHelp.TryGetSettingHelp(helpSection, key, out IReadOnlyList<ConfigHelpRecord>? helpList))
                    continue;

                for (int helpIndex = 0; helpIndex < helpList.Count; helpIndex++)
                {
                    (IConfigHelpAttribute attribute, _) = helpList[helpIndex];

                    if (attribute.Scope == ConfigScope.Arena
                        && string.IsNullOrWhiteSpace(attribute.FileName))
                    {
                        string? value = _configManager.GetStr(configHandle, sectionName, attribute.Key);
                        value ??= "<unset>";

                        StringBuilder description = _objectPoolManager.StringBuilderPool.Get();

                        try
                        {
                            description.Append(attribute.Description);
                            description.Replace('\r', ' ');
                            description.Replace('\n', ' ');

                            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

                            try
                            {
                                sb.Append($"{sectionName}:{attribute.Key}:{value}:{attribute.Min}:{attribute.Max}:{description}\r\n");

                                writer.Write(sb);
                            }
                            finally
                            {
                                _objectPoolManager.StringBuilderPool.Return(sb);
                            }
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(description);
                        }

                        // Only the first matching attribute.
                        break;
                    }
                }
            }
        }
    }
}

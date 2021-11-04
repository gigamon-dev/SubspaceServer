using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Core.Packets;
using SS.Utilities;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that handles the majority of player commands.
    /// </summary>
    /// <remarks>
    /// See the <see cref="AdminCommand"/> module for other commands that are geared towards server administration.
    /// </remarks>
    [CoreModuleInfo]
    public class PlayerCommand : IModule
    {
        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private IBalls _balls;
        private IChat _chat;
        private ICapabilityManager _capabilityManager;
        private IConfigManager _configManager;
        private ICommandManager _commandManager;
        private IGame _game;
        private IGroupManager _groupManager;
        private ILagQuery _lagQuery;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IMapData _mapData;
        private IModuleManager _mm;
        private INetwork _net;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;

        private DateTime _startedAt;

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IBalls balls,
            IChat chat,
            ICapabilityManager capabilityManager,
            IConfigManager configManager,
            ICommandManager commandManager,
            IGame game,
            IGroupManager groupManager,
            ILagQuery lagQuery,
            ILogManager logManager,
            IMainloop mainloop,
            IMapData mapData,
            IModuleManager mm,
            INetwork net,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _balls = balls ?? throw new ArgumentNullException(nameof(balls));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _capabilityManager = capabilityManager ?? throw new ArgumentNullException(nameof(capabilityManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _groupManager = groupManager ?? throw new ArgumentNullException(nameof(groupManager));
            _lagQuery = lagQuery ?? throw new ArgumentNullException(nameof(lagQuery));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mapData = mapData ?? throw new ArgumentNullException(nameof(mapData));
            _mm = mm ?? throw new ArgumentNullException(nameof(mm));
            _net = net ?? throw new ArgumentNullException(nameof(net));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _startedAt = DateTime.UtcNow;

            // TODO: do some sort of derivative of that command group thing asss does
            _commandManager.AddCommand("lag", Command_lag);
            _commandManager.AddCommand("arena", Command_arena);
            _commandManager.AddCommand("shutdown", Command_shutdown);
            _commandManager.AddCommand("recyclezone", Command_recyclezone);
            _commandManager.AddCommand("recyclearena", Command_recyclearena);
            _commandManager.AddCommand("uptime", Command_uptime);
            _commandManager.AddCommand("version", Command_version);
            _commandManager.AddCommand("sheep", Command_sheep);
            _commandManager.AddCommand("geta", Command_getX);
            _commandManager.AddCommand("getg", Command_getX);
            _commandManager.AddCommand("seta", Command_setX);
            _commandManager.AddCommand("setg", Command_setX);
            _commandManager.AddCommand("netstats", Command_netstats);
            _commandManager.AddCommand("info", Command_info);
            _commandManager.AddCommand("a", Command_a);
            _commandManager.AddCommand("aa", Command_aa);
            _commandManager.AddCommand("z", Command_z);
            _commandManager.AddCommand("az", Command_az);
            _commandManager.AddCommand("warn", Command_warn);
            _commandManager.AddCommand("reply", Command_reply);
            _commandManager.AddCommand("warpto", Command_warpto);
            _commandManager.AddCommand("shipreset", Command_shipreset);
            _commandManager.AddCommand("specall", Command_specall);
            _commandManager.AddCommand("send", Command_send);
            _commandManager.AddCommand("lsmod", Command_lsmod);
            _commandManager.AddCommand("modinfo", Command_modinfo);
            _commandManager.AddCommand("insmod", Command_insmod);
            _commandManager.AddCommand("rmmod", Command_rmmod);
            _commandManager.AddCommand("attmod", Command_attmod);
            _commandManager.AddCommand("detmod", Command_detmod);
            _commandManager.AddCommand("getgroup", Command_getgroup);
            _commandManager.AddCommand("setgroup", Command_setgroup);
            _commandManager.AddCommand("rmgroup", Command_rmgroup);
            _commandManager.AddCommand("grplogin", Command_grplogin);
            _commandManager.AddCommand("listmod", Command_listmod);
            _commandManager.AddCommand("where", Command_where);
            _commandManager.AddCommand("mapinfo", Command_mapinfo);
            _commandManager.AddCommand("ballcount", Command_ballcount);
            _commandManager.AddCommand("giveball", Command_giveball);
            _commandManager.AddCommand("moveball", Command_moveball);
            _commandManager.AddCommand("spawnball", Command_spawnball);
            _commandManager.AddCommand("ballinfo", Command_ballinfo);
            _commandManager.AddCommand("serverstats", Command_serverstats);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            _commandManager.RemoveCommand("lag", Command_lag);
            _commandManager.RemoveCommand("arena", Command_arena);
            _commandManager.RemoveCommand("shutdown", Command_shutdown);
            _commandManager.RemoveCommand("recyclezone", Command_recyclezone);
            _commandManager.RemoveCommand("recyclearena", Command_recyclearena);
            _commandManager.RemoveCommand("uptime", Command_uptime);
            _commandManager.RemoveCommand("version", Command_version);
            _commandManager.RemoveCommand("sheep", Command_sheep);
            _commandManager.RemoveCommand("geta", Command_getX);
            _commandManager.RemoveCommand("getg", Command_getX);
            _commandManager.RemoveCommand("seta", Command_setX);
            _commandManager.RemoveCommand("setg", Command_setX);
            _commandManager.RemoveCommand("netstats", Command_netstats);
            _commandManager.RemoveCommand("info", Command_info);
            _commandManager.RemoveCommand("a", Command_a);
            _commandManager.RemoveCommand("aa", Command_aa);
            _commandManager.RemoveCommand("z", Command_a);
            _commandManager.RemoveCommand("az", Command_az);
            _commandManager.RemoveCommand("warn", Command_warn);
            _commandManager.RemoveCommand("reply", Command_reply);
            _commandManager.RemoveCommand("warpto", Command_warpto);
            _commandManager.RemoveCommand("shipreset", Command_shipreset);
            _commandManager.RemoveCommand("specall", Command_specall);
            _commandManager.RemoveCommand("send", Command_send);
            _commandManager.RemoveCommand("lsmod", Command_lsmod);
            _commandManager.RemoveCommand("modinfo", Command_modinfo);
            _commandManager.RemoveCommand("insmod", Command_insmod);
            _commandManager.RemoveCommand("rmmod", Command_rmmod);
            _commandManager.RemoveCommand("attmod", Command_attmod);
            _commandManager.RemoveCommand("detmod", Command_detmod);
            _commandManager.RemoveCommand("getgroup", Command_getgroup);
            _commandManager.RemoveCommand("setgroup", Command_setgroup);
            _commandManager.RemoveCommand("rmgroup", Command_rmgroup);
            _commandManager.RemoveCommand("grplogin", Command_grplogin);
            _commandManager.RemoveCommand("listmod", Command_listmod);
            _commandManager.RemoveCommand("where", Command_where);
            _commandManager.RemoveCommand("mapinfo", Command_mapinfo);
            _commandManager.RemoveCommand("ballcount", Command_ballcount);
            _commandManager.RemoveCommand("giveball", Command_giveball);
            _commandManager.RemoveCommand("moveball", Command_moveball);
            _commandManager.RemoveCommand("spawnball", Command_spawnball);
            _commandManager.RemoveCommand("ballinfo", Command_ballinfo);
            _commandManager.RemoveCommand("serverstats", Command_serverstats);

            return true;
        }

        #endregion

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "[{-v}]",
            Description =
            "Displays lag information about you or a target player.\n" +
            "Use {-v} for more detail. The format of the ping fields is\n" +
            "\"last average (min-max)\".\n")]
        private void Command_lag(string command, string parameters, Player p, ITarget target)
        {
            Player targetPlayer = target.Type == TargetType.Player
                ? ((IPlayerTarget)target).Player
                : p;

            if (!targetPlayer.IsStandard)
            {
                _chat.SendMessage(p, $"{(targetPlayer == p ? "You" : targetPlayer.Name)} {(targetPlayer == p ? "aren't" : "isn't")} a game client.");
                return;
            }

            _lagQuery.QueryPositionPing(targetPlayer, out PingSummary positionPing);
            _lagQuery.QueryClientPing(targetPlayer, out ClientPingSummary clientPing);
            _lagQuery.QueryReliablePing(targetPlayer, out PingSummary reliablePing);
            _lagQuery.QueryPacketloss(targetPlayer, out PacketlossSummary packetloss);

            // weight reliable ping twice the S2C and C2S
            int average = (positionPing.Average + clientPing.Average + 2 * reliablePing.Average) / 4;

            string prefix = targetPlayer == p ? "lag" : targetPlayer.Name;

            if (!parameters.Contains("-v"))
            {
                _chat.SendMessage(p, $"{prefix}: avg ping: {average} ploss: s2c: {packetloss.s2c * 100:F2} c2s: {packetloss.c2s * 100:F2}");
            }
            else
            {
                _lagQuery.QueryReliableLag(targetPlayer, out ReliableLagData reliableLag);

                _chat.SendMessage(p, $"{prefix}: s2c ping: {clientPing.Current} {clientPing.Average} ({clientPing.Min}-{clientPing.Max}) (reported by client)");
                _chat.SendMessage(p, $"{prefix}: c2s ping: {positionPing.Current} {positionPing.Average} ({positionPing.Min}-{positionPing.Max}) (from position pkt times)");
                _chat.SendMessage(p, $"{prefix}: rel ping: {reliablePing.Current} {reliablePing.Average} ({reliablePing.Min}-{reliablePing.Max}) (reliable ping)");
                _chat.SendMessage(p, $"{prefix}: effective ping: {average} (average of above)");

                _chat.SendMessage(p, $"{prefix}: ploss: s2c: {packetloss.s2c * 100:F2} c2s: {packetloss.c2s * 100:F2} s2cwpn: {packetloss.s2cwpn * 100:F2}");
                _chat.SendMessage(p, $"{prefix}: reliable dups: {reliableLag.RelDups * 100 / reliableLag.C2SN:F2}% reliable resends: {reliableLag.Retries * 100 / reliableLag.S2CN:F2}%");
                _chat.SendMessage(p, $"{prefix}: s2c slow: {clientPing.S2CSlowCurrent}/{clientPing.S2CSlowTotal} s2c fast: {clientPing.S2CFastCurrent}/{clientPing.S2CFastTotal}");

                SendCommonBandwidthInfo(p, targetPlayer, DateTime.UtcNow - targetPlayer.ConnectTime, prefix, false);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-a}] [{-t}]",
            Description =
            "Lists the available arenas. Specifying {-a} will also include\n" +
            "empty arenas that the server knows about. The {-t} switch forces\n" +
            "the output to be in text even for regular clients (useful when using\n" +
            "the Continuum chat window).")]
        private void Command_arena(string command, string parameters, Player p, ITarget target)
        {
            // TODO: add support for chat output
            // TODO: add support for -a argument
            //bool isChatOutput = p.Type == ClientType.Chat || parameters.Contains("-t");
            bool includePrivateArenas = _capabilityManager.HasCapability(p, Constants.Capabilities.SeePrivArena);

            Span<byte> bufferSpan = stackalloc byte[1024];

            // Write header
            bufferSpan[0] = (byte)S2CPacketType.Arena;
            int length = 1;

            _arenaManager.Lock();

            try
            {
                // refresh arena counts
                _arenaManager.GetPopulationSummary(out _, out _);

                foreach (Arena arena in _arenaManager.ArenaList)
                {
                    int nameLength = StringUtils.DefaultEncoding.GetByteCount(arena.Name) + 1; // +1 because the name in the packet is null terminated
                    int additionalLength = nameLength + 2;

                    if (length + additionalLength > (Constants.MaxPacket - ReliableHeader.Length))
                    {
                        break;
                    }

                    if (arena.Status == ArenaState.Running
                        && (!arena.IsPrivate || includePrivateArenas || p.Arena == arena))
                    {
                        // arena name
                        Span<byte> remainingSpan = bufferSpan[length..];
                        remainingSpan = remainingSpan[remainingSpan.WriteNullTerminatedString(arena.Name)..];

                        // player count (a negative value denotes the player's current arena)
                        Span<byte> playerCountSpan = remainingSpan.Slice(0, 2);
                        BinaryPrimitives.WriteInt16LittleEndian(playerCountSpan, arena == p.Arena ? (short)-arena.Total : (short)arena.Total);

                        length += additionalLength;
                    }
                }
            }
            finally
            {
                _arenaManager.Unlock();
            }

            // TODO: additional arena list logic (-a argument)

            _net.SendToOne(p, bufferSpan.Slice(0, length), NetSendFlags.Reliable);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-r}]",
            Description =
            "Immediately shuts down the server, exiting with {EXIT_NONE}. If\n" +
            "{-r} is specified, exit with {EXIT_RECYCLE} instead. The {run-asss}\n" +
            "script, if it is being used, will notice {EXIT_RECYCLE} and restart\n" +
            "the server.")]
        private void Command_shutdown(string command, string parameters, Player p, ITarget target)
        {
            ExitCode code = string.Equals(parameters, "-r", StringComparison.OrdinalIgnoreCase)
                ? ExitCode.Recycle
                : ExitCode.None;

            _mainloop.Quit(code);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description =
            "Immediately shuts down the server, exiting with {EXIT_RECYCLE}. The\n" +
            "{run-asss} script, if it is being used, will notice {EXIT_RECYCLE}\n" +
            "and restart the server.")]
        private void Command_recyclezone(string command, string parameters, Player p, ITarget target)
        {
            _mainloop.Quit(ExitCode.Recycle);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Recycles the current arena without kicking players off.")]
        private void Command_recyclearena(string command, string parameters, Player p, ITarget target)
        {
            if (!_arenaManager.RecycleArena(p.Arena))
            {
                _chat.SendMessage(p, "Arena recycle failed; check the log for details.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Displays how long the server has been running.")]
        private void Command_uptime(string command, string parameters, Player p, ITarget target)
        {
            TimeSpan ts = DateTime.UtcNow - _startedAt;

            _chat.SendMessage(p, $"uptime: {ts.Days} days {ts.Hours} hours {ts.Minutes} minutes {ts.Seconds} seconds");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-v}]",
            Description =
            "Prints out information about the server.\n" +
            "For staff members, it will print more detailed version information.\n" +
            "If staff members specify the {-v} arg, it will print even more verbose information.")]
        private void Command_version(string command, string parameters, Player p, ITarget target)
        {
            _chat.SendMessage(p, $"Subspace Server .NET");

            if (_capabilityManager.HasCapability(p, Constants.Capabilities.IsStaff))
            {
                _chat.SendMessage(p, $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                    $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} " +
                    $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!parameters.Contains("v")
                        && (assembly.FullName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                            || assembly.FullName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                            || assembly.FullName.StartsWith("netstandard,", StringComparison.OrdinalIgnoreCase)
                        ))
                    {
                        continue;
                    }

                    _chat.SendMessage(p, $"{assembly.FullName}");
                }
            }
        }

        private void Command_sheep(string command, string parameters, Player p, ITarget target)
        {
            if (target.Type != TargetType.Arena)
                return;

            string sheepMessage = _configManager.GetStr(p.Arena.Cfg, "Misc", "SheepMessage");

            if (sheepMessage != null)
                _chat.SendMessage(p, ChatSound.Sheep, sheepMessage);
            else
                _chat.SendMessage(p, ChatSound.Sheep, "Sheep successfully cloned -- hello Dolly");
        }

        [CommandHelp(
            Command = "geta",
            Targets = CommandTarget.None,
            Args = "section:key",
            Description = "Displays the value of an arena setting. Make sure there are no\n" +
            "spaces around the colon.")]
        [CommandHelp(
            Command = "getg",
            Targets = CommandTarget.None,
            Args = "section:key",
            Description = "Displays the value of a global setting. Make sure there are no\n" +
            "spaces around the colon.")]
        private void Command_getX(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return;

            ConfigHandle ch = string.Equals(command, "geta", StringComparison.Ordinal) ? p.Arena.Cfg : _configManager.Global;
            string result = _configManager.GetStr(ch, parameters, null);
            if (result != null)
            {
                _chat.SendMessage(p, $"{parameters}={result}");
            }
            else
            {
                _chat.SendMessage(p, $"{parameters} not found.");
            }
        }

        [CommandHelp(
            Command = "seta",
            Targets = CommandTarget.None,
            Args = "[{-t}] section:key=value",
            Description = "Sets the value of an arena setting. Make sure there are no\n" +
            "spaces around either the colon or the equals sign. A {-t} makes\n" +
            "the setting temporary.\n")]
        [CommandHelp(
            Command = "setg",
            Targets = CommandTarget.None,
            Args = "[{-t}] section:key=value",
            Description = "Sets the value of a global setting. Make sure there are no\n" +
            "spaces around either the colon or the equals sign. A {-t} makes\n" +
            "the setting temporary.\n")]
        private void Command_setX(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return;

            ConfigHandle ch = string.Equals(command, "seta", StringComparison.Ordinal) ? p.Arena.Cfg : _configManager.Global;
            bool permanent = true;

            ReadOnlySpan<char> line = parameters.AsSpan();
            if (line.StartsWith("-t"))
            {
                permanent = false;
                line = line[2..];
            }

            Configuration.ConfFile.ParseConfProperty(line, out string section, out string key, out ReadOnlySpan<char> value, out _);
            _configManager.SetStr(ch, section, key, value.ToString(), $"Set by {p.Name} on {DateTime.Now}", permanent);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Prints out some statistics from the network layer.")]
        private void Command_netstats(string command, string parameters, Player p, ITarget target)
        {
            ulong secs = Convert.ToUInt64((DateTime.UtcNow - _startedAt).TotalSeconds);

            IReadOnlyNetStats stats = _net.GetStats();
            _chat.SendMessage(p, $"netstats: pings={stats.PingsReceived}  pkts sent={stats.PacketsSent}  pkts recvd={stats.PacketsReceived}");

            // IP Header (20 bytes) + UDP Header (8 bytes) = 28 bytes total overhead for each packet
            ulong bwout = (stats.BytesSent + stats.PacketsSent * 28) / secs;
            ulong bwin = (stats.BytesReceived + stats.PacketsReceived * 28) / secs;
            _chat.SendMessage(p, $"netstats: bw out={bwout}  bw in={bwin}");

            _chat.SendMessage(p, $"netstats: buffers used={stats.BuffersUsed}/{stats.BuffersTotal} ({(double)stats.BuffersUsed / stats.BuffersTotal:p})");

            _chat.SendMessage(p, $"netstats: grouped=" +
                $"{stats.GroupedStats0}/" +
                $"{stats.GroupedStats1}/" +
                $"{stats.GroupedStats2}/" +
                $"{stats.GroupedStats3}/" +
                $"{stats.GroupedStats4}/" +
                $"{stats.GroupedStats5}/" +
                $"{stats.GroupedStats6}/" +
                $"{stats.GroupedStats7}");

            _chat.SendMessage(p, $"netstats: rel grouped=" +
                $"{stats.RelGroupedStats0}/" +
                $"{stats.RelGroupedStats1}/" +
                $"{stats.RelGroupedStats2}/" +
                $"{stats.RelGroupedStats3}/" +
                $"{stats.RelGroupedStats4}/" +
                $"{stats.RelGroupedStats5}/" +
                $"{stats.RelGroupedStats6}/" +
                $"{stats.RelGroupedStats7}");

            _chat.SendMessage(p, $"netstats: pri=" +
                $"{stats.PriorityStats0}/" +
                $"{stats.PriorityStats1}/" +
                $"{stats.PriorityStats2}/" +
                $"{stats.PriorityStats3}/" +
                $"{stats.PriorityStats4}");
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = null,
            Description =
            "Displays various information on the target player, including which\n" +
            "client they are using, their resolution, IP address, how long they have\n" +
            "been connected, and bandwidth usage information.")]
        private void Command_info(string command, string parameters, Player p, ITarget target)
        {
            if (target == null
                || target.Type != TargetType.Player
                || target is not IPlayerTarget playerTarget)
            {
                _chat.SendMessage(p, "info: must use on a player");
                return;
            }

            Player t = playerTarget.Player;
            if (t == null)
                return;

            string prefix = t.Name;
            TimeSpan connectedTimeSpan = DateTime.UtcNow - t.ConnectTime;

            _chat.SendMessage(p, $"{prefix}: pid={t.Id} name='{t.Name}' squad='{t.Squad}' " +
                $"auth={(t.Flags.Authenticated ? 'Y' : 'N')} ship={t.Ship} freq={t.Freq}");
            _chat.SendMessage(p, $"{prefix}: arena={(t.Arena != null ? t.Arena.Name : "(none)")} " +
                $"client={t.ClientName} res={t.Xres}x{t.Yres} onFor={connectedTimeSpan} " +
                $"connectAs={(!string.IsNullOrWhiteSpace(t.ConnectAs) ? t.ConnectAs : "<default>")}");

            if (t.IsStandard)
            {
                SendCommonBandwidthInfo(p, t, connectedTimeSpan, prefix, true);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = "<text>",
            Description = "Displays the text as an arena (green) message to the targets.")]
        private void Command_a(string command, string parameters, Player p, ITarget target, ChatSound sound)
        {
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.TargetToSet(target, set);
                sb.Append(parameters);
                sb.Append(" -");
                sb.Append(p.Name);
                _chat.SendSetMessage(set, sound, sb);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = "<text>",
            Description = "Displays the text as an anonymous arena (green) message to the targets.")]
        private void Command_aa(string command, string parameters, Player p, ITarget target, ChatSound sound)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.TargetToSet(target, set);
                _chat.SendSetMessage(set, sound, parameters);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<text>",
            Description = "Displays the text as an arena (green) message to the whole zone.")]
        private void Command_z(string command, string parameters, Player p, ITarget target, ChatSound sound)
        {
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                sb.Append(parameters);
                sb.Append(" -");
                sb.Append(p.Name);

                _chat.SendArenaMessage(null, sound, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }

            // TODO: peer
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<text>",
            Description = "Displays the text as an anonymous arena (green) message to the whole zone.")]
        private void Command_az(string command, string parameters, Player p, ITarget target, ChatSound sound)
        {
            _chat.SendArenaMessage(null, sound, parameters);

            // TODO: peer
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = "<message>",
            Description = "Sends a red warning message to a player.")]
        private void Command_warn(string command, string parameters, Player p, ITarget target)
        {
            if (target.Type != TargetType.Player || target is not IPlayerTarget playerTarget)
            {
                _chat.SendMessage(p, "You must target a player.");
                return;
            }

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                set.Add(playerTarget.Player);

                if (_capabilityManager.HasCapability(p, Constants.Capabilities.IsStaff))
                {
                    _chat.SendAnyMessage(set, ChatMessageType.SysopWarning, ChatSound.Beep1, null, $"WARNING: {parameters} -{p.Name}");
                }
                else
                {
                    _chat.SendAnyMessage(set, ChatMessageType.SysopWarning, ChatSound.Beep1, null, $"WARNING: {parameters}");
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }

            _chat.SendMessage(p, $"Player '{playerTarget.Player.Name}' has been warned.");
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = "<message>",
            Description = "Sends a private message to a player.\n" +
            "Useful for logging replies to moderator help requests.")]
        private void Command_reply(string command, string parameters, Player p, ITarget target)
        {
            if (target.Type != TargetType.Player || target is not IPlayerTarget playerTarget)
            {
                _chat.SendMessage(p, "You must target a player.");
                return;
            }

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                set.Add(playerTarget.Player);
                _chat.SendAnyMessage(set, ChatMessageType.Private, ChatSound.None, p, parameters);
                _chat.SendMessage(p, $"Private message sent to player '{playerTarget.Player.Name}'.");
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        private void SendCommonBandwidthInfo(Player p, Player t, TimeSpan connectedTimeSpan, string prefix, bool includeSensitive)
        {
            NetClientStats stats = _net.GetClientStats(t);

            if (includeSensitive)
            {
                _chat.SendMessage(p, $"{prefix}: ip:{stats.IPEndPoint.Address} port:{stats.IPEndPoint.Port} " +
                    $"encName={stats.EncryptionName} macId={t.MacId} permId={t.PermId}");
            }

            int ignoringwpns = (int)(100f * _game.GetIgnoreWeapons(t));

            _chat.SendMessage(p,
                $"{prefix}: " +
                $"ave bw in/out={(stats.BytesReceived / connectedTimeSpan.TotalSeconds):N0}/{(stats.BytesSent / connectedTimeSpan.TotalSeconds):N0} " +
                $"ignoringWpns={ignoringwpns} dropped={stats.PacketsDropped}");

            // TODO: bwlimit info

            if (t.Flags.NoShip)
                _chat.SendMessage(p, $"{prefix}: lag too high to play");

            if (t.Flags.NoFlagsBalls)
                _chat.SendMessage(p, $"{prefix}: lag too high to carry flags or balls");
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = "<x xoord> <y coord>",
            Description = "Warps target player(s) to an x,y coordinate.")]
        private void Command_warpto(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return;

            ReadOnlySpan<char> coordsSpan = parameters.AsSpan().Trim();

            int index = coordsSpan.IndexOf(' ');
            if (index == -1)
                return;

            if (!short.TryParse(coordsSpan.Slice(0, index), out short x))
                return;

            if (!short.TryParse(coordsSpan[(index + 1)..], out short y))
                return;

            if (x == 0 && y == 0)
                return;

            _game.WarpTo(target, x, y);
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = null,
            Description = "Resets the ship of the target player(s).")]
        private void Command_shipreset(string command, string parameters, Player p, ITarget target)
        {
            _game.ShipReset(target);
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = null,
            Description = "Sends all of the targets to spectator mode.")]
        private void Command_specall(string command, string parameters, Player p, ITarget target)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.TargetToSet(target, set);

                foreach (Player player in set)
                    _game.SetShipAndFreq(player, ShipType.Spec, p.Arena.SpecFreq);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = "<arena name>",
            Description = "Sends target player to the named arena. (Works on Continuum users only.)")]
        private void Command_send(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return;

            if (target.Type != TargetType.Player || target is not IPlayerTarget playerTarget)
                return;

            Player targetPlayer = playerTarget.Player;
            switch (targetPlayer.Type)
            {
                case ClientType.Continuum:
                case ClientType.Chat:
                case ClientType.VIE:
                    _arenaManager.SendToArena(p, parameters, 0, 0);
                    break;

                default:
                    _chat.SendMessage(p, "You can only use ?send on players using Continuum, Subspace or chat clients.");
                    break;
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-a}] [{-s}] [<text>]",
            Description =
            "Lists all the modules currently loaded into the server. With {-a}, lists\n" +
            "only modules attached to this arena. With {-s}, sorts by name.\n" +
            "With optional `text`, limits modules displayed to those whose names\n" +
            "contain the given text.")]
        private void Command_lsmod(string command, string parameters, Player p, ITarget target)
        {
            bool sort = false;
            string substr = null;

            Arena arena = null;

            if (!string.IsNullOrWhiteSpace(parameters))
            {
                string[] args = parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (string arg in args)
                {
                    if (string.Equals(arg, "-a", StringComparison.OrdinalIgnoreCase))
                    {
                        if (target.Type == TargetType.Arena
                            && target is IArenaTarget arenaTarget)
                        {
                            arena = arenaTarget.Arena;
                        }
                    }
                    else if (string.Equals(arg, "-s", StringComparison.OrdinalIgnoreCase))
                    {
                        sort = true;
                    }
                    else
                    {
                        substr = arg;
                    }
                }
            }

            LinkedList<string> modulesList = new();

            _mm.EnumerateModules(
                (moduleType, _) =>
                {
                    string name = moduleType.FullName;
                    if (substr == null || name.Contains(substr))
                        modulesList.AddLast(name);
                },
                arena);

            IEnumerable<string> modules = modulesList;

            if (sort)
            {
                modules = from str in modules
                          orderby str
                          select str;
            }

            StringBuilder sb = new();
            foreach (string str in modules)
            {
                if (sb.Length > 0)
                    sb.Append(", ");

                sb.Append(str);
            }

            _chat.SendWrappedText(p, sb.ToString());
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<module name>",
            Description =
            "Displays information about the specified module. This might include a\n" +
            "version number, contact information for the author, and a general\n" +
            "description of the module.")]
        private void Command_modinfo(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return;

            var infoArray = _mm.GetModuleInfo(parameters);

            if (infoArray.Length > 0)
            {
                foreach (var info in infoArray)
                {
                    _chat.SendMessage(p, info.ModuleTypeName);
                    _chat.SendMessage(p, $"  Type: {info.ModuleQualifiedName}");
                    _chat.SendMessage(p, $"  Assembly Path: {info.AssemblyPath}");
                    _chat.SendMessage(p, $"  Module Type: {(info.IsPlugin ? "plug-in" : "built-in")}");

                    if (info.AttachedArenas.Length > 0)
                    {
                        string attachedArenas = string.Join(", ", info.AttachedArenas.Select(a => a.Name));
                        _chat.SendMessage(p, $"  Attached Arenas: {attachedArenas}");
                    }

                    _chat.SendMessage(p, $"  Description:");
                    string[] tokens = info.Description.Split('\n');
                    foreach (string token in tokens)
                    {
                        _chat.SendMessage(p, $"    {token}");
                    }
                }
            }
            else
            {
                _chat.SendMessage(p, $"Module '{parameters}' not found.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<module name>",
            Description = "Immediately loads the specified module into the server.")]
        private void Command_insmod(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return;

            string moduleTypeName;
            string path;

            string[] tokens = parameters.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 1)
            {
                moduleTypeName = tokens[0];
                path = null;
            }
            else if (tokens.Length == 2)
            {
                moduleTypeName = tokens[0];
                path = tokens[1];
                path = path.Trim('"', '\'', ' ');
            }
            else
            {
                return;
            }

            if (_mm.LoadModule(moduleTypeName, path))
                _chat.SendMessage(p, $"Module '{moduleTypeName}' loaded.");
            else
                _chat.SendMessage(p, $"Failed to load module '{moduleTypeName}'.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<module name>",
            Description = "Attempts to unload the specified module from the server.")]
        private void Command_rmmod(string command, string parameters, Player p, ITarget target)
        {
            if (string.IsNullOrWhiteSpace(parameters))
                return;

            if (_mm.UnloadModule(parameters))
                _chat.SendMessage(p, $"Module '{parameters}' unloaded.");
            else
                _chat.SendMessage(p, $"Failed to unload module '{parameters}'.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-d}] <module name>",
            Description =
            "Attaches the specified module to this arena. Or with {-d},\n" +
            "detaches the module from the arena.")]
        private void Command_attmod(string command, string parameters, Player p, ITarget target)
        {
            bool detach = false;
            string module = parameters;

            if (!string.IsNullOrWhiteSpace(parameters))
            {
                string[] tokens = parameters.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 2 && string.Equals(tokens[0], "-d"))
                {
                    detach = true;
                    module = tokens[1];
                }
            }

            AttachDetachModule(p, module, detach);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<module name>",
            Description = "Detaches the module from the arena.")]
        private void Command_detmod(string command, string parameters, Player p, ITarget target)
        {
            AttachDetachModule(p, parameters, true);
        }

        private void AttachDetachModule(Player p, string module, bool detach)
        {
            if (p == null)
                return;

            if (detach)
            {
                if (_mm.DetachModule(module, p.Arena))
                    _chat.SendMessage(p, $"Module '{module}' detached.");
                else
                    _chat.SendMessage(p, $"Failed to detach module '{module}'.");
            }
            else
            {
                if (_mm.AttachModule(module, p.Arena))
                    _chat.SendMessage(p, $"Module '{module}' attached.");
                else
                    _chat.SendMessage(p, $"Failed to attach module '{module}'.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.None,
            Args = null,
            Description = "Displays the group of the player, or if none specified, you.")]
        private void Command_getgroup(string command, string parameters, Player p, ITarget target)
        {
            if (target.Type == TargetType.Player && target is IPlayerTarget playerTarget)
            {
                Player targetPlayer = playerTarget.Player;
                _chat.SendMessage(p, $"{targetPlayer.Name} is in group {_groupManager.GetGroup(targetPlayer)}.");
            }
            else
            {
                _chat.SendMessage(p, $"You are in group {_groupManager.GetGroup(p)}.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = "[{-a}] [{-p}] <group name>",
            Description = "Assigns the group given as an argument to the target player. The player\n" +
            "must be in group {default}, or the server will refuse to change his\n" +
            "group. Additionally, the player giving the command must have an\n" +
            "appropriate capability: {setgroup_foo}, where {foo} is the\n" +
            "group that he's trying to set the target to.\n\n" +
            "The optional {-p} means to assign the group permanently. Otherwise, when\n" +
            "the target player logs out or changes arenas, the group will be lost.\n\n" +
            "The optional {-a} means to make the assignment local to the current\n" +
            "arena, rather than being valid in the entire zone.")]
        private void Command_setgroup(string command, string parameters, Player p, ITarget target)
        {
            Player targetPlayer = (target as IPlayerTarget)?.Player;
            if (targetPlayer == null)
                return;

            bool permanent = false;
            bool global = true;
            string groupName = null;

            string[] parameterArray = parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string parameter in parameterArray)
            {
                if (string.Equals(parameter, "-p"))
                    permanent = true;
                else if (string.Equals(parameter, "-a"))
                    global = false;
                else
                    groupName = parameter;
            }

            if (string.IsNullOrWhiteSpace(groupName))
                return;

            if (!_capabilityManager.HasCapability(p, $"higher_than_{groupName}"))
            {
                _chat.SendMessage(p, $"You don't have permission to give people group {groupName}.");
                _logManager.LogP(LogLevel.Warn, nameof(PlayerCommand), p, $"Doesn't have permission to set group '{groupName}'.");
                return;
            }

            // make sure the target isn't in a group already
            string currentGroup = _groupManager.GetGroup(targetPlayer);
            if (!string.Equals(currentGroup, "default"))
            {
                _chat.SendMessage(p, $"Player {targetPlayer.Name} already has a group. You need to use ?rmgroup first.");
                _logManager.LogP(LogLevel.Warn, nameof(PlayerCommand), p, $"Tried to set the group of [{targetPlayer.Name}], who is in '{currentGroup}' already, to '{groupName}'.");
                return;
            }

            if (permanent)
            {
                _groupManager.SetPermGroup(targetPlayer, groupName, global, $"Set by {p.Name} on {DateTime.Now}.");
                _chat.SendMessage(p, $"{targetPlayer.Name} is now in group {groupName}.");
                _chat.SendMessage(targetPlayer, $"You have been assigned to group {groupName} by {p.Name}.");
            }
            else
            {
                _groupManager.SetTempGroup(targetPlayer, groupName);
                _chat.SendMessage(p, $"{targetPlayer.Name} is now temporarily in group {groupName}.");
                _chat.SendMessage(targetPlayer, $"You have been temporarily assigned to group {groupName} by {p.Name}.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = null,
            Description = "Removes the group from a player, returning him to group 'default'. If\n" +
            "the group was assigned for this session only, then it will be removed\n" +
            "for this session; if it is a global group, it will be removed globally;\n" +
            "and if it is an arena group, it will be removed for this arena.")]
        private void Command_rmgroup(string command, string parameters, Player p, ITarget target)
        {
            Player targetPlayer = (target as IPlayerTarget)?.Player;
            if (targetPlayer == null)
                return;

            string currentGroup = _groupManager.GetGroup(targetPlayer);
            if (!_capabilityManager.HasCapability(p, $"higher_than_{currentGroup}"))
            {
                _chat.SendMessage(p, $"You don't have permission to take away group {currentGroup}.");
                _logManager.LogP(LogLevel.Warn, nameof(PlayerCommand), p, $"Doesn't have permission to take away group '{currentGroup}'.");
                return;
            }

            _groupManager.RemoveGroup(targetPlayer, $"Set by {p.Name} on {DateTime.Now}");

            _chat.SendMessage(p, $"{targetPlayer.Name} has been removed from group {currentGroup}.");
            _chat.SendMessage(targetPlayer, $"You have been removed from group {currentGroup}.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<group name> <password>",
            Description = "Logs you in to the specified group, if the password is correct.")]
        private void Command_grplogin(string command, string parameters, Player p, ITarget target)
        {
            string[] parameterArray = parameters.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parameterArray.Length != 2)
            {
                _chat.SendMessage(p, "You must specify a group name and password.");
            }
            else if (_groupManager.CheckGroupPassword(parameterArray[0], parameterArray[1]))
            {
                _groupManager.SetTempGroup(p, parameterArray[0]);
                _chat.SendMessage(p, $"You are now in group {parameterArray[0]}.");
            }
            else
            {
                _chat.SendMessage(p, $"Bad password for group {parameterArray[0]}.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Lists all staff members logged on, which arena they are in, and\n" +
            "which group they belong to.\n")]
        private void Command_listmod(string command, string parameters, Player p, ITarget target)
        {
            bool canSeePrivateArenas = _capabilityManager.HasCapability(p, Constants.Capabilities.SeePrivArena);
            bool canSeeAllStaff = _capabilityManager.HasCapability(p, Constants.Capabilities.SeeAllStaff);

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.PlayerList)
                    {
                        if (player.Status != PlayerState.Playing)
                            continue;

                        string group = _groupManager.GetGroup(player);
                        string format;

                        if (_capabilityManager.HasCapability(player, Constants.Capabilities.IsStaff))
                        {
                            format = ": {0,20} {1,10} {2}";
                        }
                        else if (canSeeAllStaff
                            && !string.Equals(group, "default", StringComparison.Ordinal)
                            && !string.Equals(group, "none", StringComparison.Ordinal))
                        {
                            format = ": {0,20} {1,10} ({2})";
                        }
                        else
                        {
                            format = null;
                        }

                        if (format != null)
                        {
                            sb.Clear();
                            sb.AppendFormat(
                                format,
                                player.Name,
                                (!player.Arena.IsPrivate || canSeePrivateArenas || p.Arena == player.Arena) ? player.Arena.Name : "(private)",
                                group);

                            _chat.SendMessage(p, sb);                                
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = null,
            Description = "Displays the current location (on the map) of the target player.")]
        private void Command_where(string command, string parameters, Player p, ITarget target)
        {
            Player t = null;

            if (target != null
                && target.Type == TargetType.Player
                && target is IPlayerTarget playerTarget)
            {
                t = playerTarget.Player;
            }

            if (t == null)
                t = p;

            // right shift by 4 is divide by 16 (each tile is 16 pixels)
            int x = t.Position.X >> 4;
            int y = t.Position.Y >> 4;

            string name = (t == p) ? "You" : t.Name;
            string verb = (t == p) ? "are" : "is";

            if (t.IsStandard)
            {
                _chat.SendMessage(p, $"{name} {verb} at {(char)('A' + (x * 20 / 1024))}{(y * 20 / 1024 + 1)} ({x},{y})");
            }
            else
            {
                _chat.SendMessage(p, $"{name} {verb} not using a playable client.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Arena,
            Args = null,
            Description = "Displays information about the map in this arena.")]
        private void Command_mapinfo(string command, string parameters, Player p, ITarget target)
        {
            Arena arena = p.Arena;
            if (arena == null)
                return;

            string fileName = _mapData.GetMapFilename(arena, null);

            _chat.SendMessage(p, $"LVL file loaded from '{(!string.IsNullOrWhiteSpace(fileName) ? fileName : "<nowhere>")}'.");

            const string NotSet = "<not set>";

            _chat.SendMessage(p,
                $"name: {_mapData.GetAttribute(arena, MapAttributeKeys.Name) ?? NotSet}, " +
                $"version: {_mapData.GetAttribute(arena, MapAttributeKeys.Version) ?? NotSet}");

            _chat.SendMessage(p,
                $"map creator: {_mapData.GetAttribute(arena, MapAttributeKeys.MapCreator) ?? NotSet}, " +
                $"tileset creator: {_mapData.GetAttribute(arena, MapAttributeKeys.TilesetCreator) ?? NotSet}, " +
                $"program: {_mapData.GetAttribute(arena, MapAttributeKeys.Program) ?? NotSet}");

            var errors = _mapData.GetErrors(arena);

            _chat.SendMessage(p,
                $"tiles:{_mapData.GetTileCount(arena)} " +
                $"flags:{_mapData.GetFlagCount(arena)} " +
                $"regions:{_mapData.GetRegionCount(arena)} " +
                $"errors:{errors.Count}");

            if (errors.Count > 0 && _capabilityManager.HasCapability(p, Constants.Capabilities.IsStaff))
            {
                _chat.SendMessage(p, "Error details:");
                foreach (var error in errors)
                {
                    _chat.SendMessage(p, $"- {error}");
                }
            }

            // TODO: estimated memory stats
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[<new # of balls> | +<balls to add> | -<balls to remove>]",
            Description =
            "Displays or changes the number of balls in the arena.\n" +
            "A number without a plus or minus sign is taken as a new count.\n" +
            "A plus signifies adding that many, and a minus removes that many.\n" +
            "Continuum currently supports only eight balls.")]
        private void Command_ballcount(string command, string parameters, Player p, ITarget target)
        {
            Arena arena = p.Arena;
            if (arena == null)
                return;

            if (string.IsNullOrWhiteSpace(parameters))
            {
                if (_balls.TryGetBallSettings(arena, out BallSettings ballSettings))
                {
                    _chat.SendMessage(p, $"Ball count: {ballSettings.BallCount}");
                }
            }
            else if (parameters[0] == '+' || parameters[0] == '-')
            {
                if (int.TryParse(parameters, out int numToAddOrRemove)
                    && _balls.TryGetBallSettings(arena, out BallSettings ballSettings))
                {
                    _balls.TrySetBallCount(arena, ballSettings.BallCount + numToAddOrRemove);
                }
            }
            else
            {
                if (int.TryParse(parameters, out int newBallCount))
                {
                    _balls.TrySetBallCount(arena, newBallCount);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "[{-f} [<ball id>]",
            Description =
            "Moves the specified ball to you, or a targeted player.\n" +
            "If no ball is specified, ball id = is assumed.\n" +
            "If -f is specified, the ball is forced onto the player and there will be no shot timer, and if the player is already carrying a ball it will be dropped where they are standing.\n" +
            "If -f is not specified, then the ball is simply moved underneath a player for him to pick up, but any balls already carried are not dropped.")]
        private void Command_giveball(string command, string parameters, Player p, ITarget target)
        {
            Arena arena = p.Arena;
            if (arena == null)
                return;

            bool force = false;
            byte ballId = 0;

            ReadOnlySpan<char> remaining = parameters.AsSpan();

            do
            {
                ReadOnlySpan<char> token = remaining.GetToken(' ', out remaining);
                if (token.IsEmpty)
                    continue;

                if (token.Equals("-f", StringComparison.Ordinal))
                {
                    force = true;
                }
                else if (!byte.TryParse(token, out ballId))
                {
                    _chat.SendMessage(p, "Invalid ball ID.");
                    return;
                }
            }
            while (!remaining.IsEmpty);

            if (!_balls.TryGetBallSettings(arena, out BallSettings ballSettings)
                || ballId >= ballSettings.BallCount)
            {
                _chat.SendMessage(p, $"Ball {ballId} doesn't exist. Use ?ballcount to add balls to the arena.");
                return;
            }

            Player targetPlayer = target.Type == TargetType.Player ? ((IPlayerTarget)target).Player : p;

            if (targetPlayer.Ship == ShipType.Spec)
            {
                if (targetPlayer == p)
                    _chat.SendMessage(p, "You are in spec.");
                else
                    _chat.SendMessage(p, $"{targetPlayer.Name} is in spec.");
            }
            else if (targetPlayer.Arena != p.Arena || targetPlayer.Status != PlayerState.Playing)
            {
                _chat.SendMessage(p, $"{targetPlayer.Name} is not in this arena.");
            }
            else
            {
                BallData newBallData = new()
                {
                    State = BallState.OnMap,
                    Carrier = null,
                    Freq = -1,
                    XSpeed = 0,
                    YSpeed = 0,
                    X = targetPlayer.Position.X,
                    Y = targetPlayer.Position.Y,
                    Time = ServerTick.Now,
                    LastUpdate = 0,
                };

                if (force)
                {
                    for (byte i = 0; i < ballSettings.BallCount; i++)
                    {
                        if (_balls.TryGetBallData(arena, i, out BallData bd)
                            && bd.Carrier == targetPlayer
                            && bd.State == BallState.Carried)
                        {
                            _balls.TryPlaceBall(arena, i, ref newBallData);
                        }
                    }

                    newBallData.State = BallState.Carried;
                    newBallData.Carrier = targetPlayer;
                    newBallData.Freq = targetPlayer.Freq;
                    newBallData.Time = 0;
                }

                if (_balls.TryPlaceBall(arena, ballId, ref newBallData))
                {
                    if (targetPlayer != p)
                    {
                        _chat.SendMessage(p, $"Gave ball {ballId} to {targetPlayer.Name}.");
                    }
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<ball id> <x-coord> <y-coord>",
            Description = "Move the specified ball to the specified coordinates.")]
        private void Command_moveball(string command, string parameters, Player p, ITarget target)
        {
            Arena arena = p.Arena;
            if (arena == null)
                return;

            ReadOnlySpan<char> token = parameters.AsSpan().GetToken(' ', out ReadOnlySpan<char> remaining);
            if (token.IsEmpty || !byte.TryParse(token, out byte ballId))
            {
                _chat.SendMessage(p, "Invalid ball ID.");
                return;
            }

            if (!_balls.TryGetBallSettings(arena, out BallSettings ballSettings)
                || ballId >= ballSettings.BallCount)
            {
                _chat.SendMessage(p, $"Ball {ballId} doesn't exist. Use ?ballcount to add balls to the arena.");
                return;
            }

            token = remaining.GetToken(' ', out remaining);
            if (token.IsEmpty || !short.TryParse(token, out short x) || x < 0 || x >= 1024)
            {
                _chat.SendMessage(p, "Invalid x-coordinate.");
                return;
            }

            if (!short.TryParse(remaining, out short y) || y < 0 || y >= 1024)
            {
                _chat.SendMessage(p, "Invalid y-coordinate.");
                return;
            }

            BallData newBallData = new()
            {
                State = BallState.OnMap,
                Carrier = null,
                Freq = -1,
                XSpeed = 0,
                YSpeed = 0,
                X = (short)((x << 4) + 8),
                Y = (short)((y << 4) + 8),
                Time = ServerTick.Now,
                LastUpdate = 0,
            };

            if (_balls.TryPlaceBall(arena, ballId, ref newBallData))
            {
                _chat.SendMessage(p, $"Moved ball {ballId} to ({x},{y}).");
            }
            else
            {
                _chat.SendMessage(p, $"Failed to moved ball {ballId} to ({x},{y}).");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[<ball id>]",
            Description =
            "Resets the specified existing ball back to its spawn location.\n" +
            "If no ball is specified, ball id 0 is assumed.")]
        private void Command_spawnball(string command, string parameters, Player p, ITarget target)
        {
            Arena arena = p.Arena;
            if (arena == null)
                return;

            int ballId;

            if (string.IsNullOrWhiteSpace(parameters))
            {
                ballId = 0;
            }
            else if (!int.TryParse(parameters, out ballId))
            {
                _chat.SendMessage(p, "Invalid ball ID.");
                return;
            }

            if (_balls.TrySpawnBall(arena, ballId))
            {
                _chat.SendMessage(p, $"Respawned ball {ballId}.");
            }
            else
            {
                _chat.SendMessage(p, $"Failed to respawn ball {ballId}. Check that the ball exists. Use ?ballcount to add more balls to the arena.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Displays the last known position of balls, as well as the player who is carrying it or who fired it, if applicable.")]
        private void Command_ballinfo(string command, string parameters, Player p, ITarget target)
        {
            Arena arena = p.Arena;
            if (arena == null)
                return;

            if (!_balls.TryGetBallSettings(arena, out BallSettings ballSettings))
                return;

            for (int ballId = 0; ballId < ballSettings.BallCount; ballId++)
            {
                if (_balls.TryGetBallData(arena, ballId, out BallData ballData))
                {
                    var x = (ballData.X >> 4) * 20 / 1024;
                    var y = (ballData.X >> 4) * 20 / 1024;

                    switch (ballData.State)
                    {
                        case BallState.OnMap:
                            if (ballData.Carrier != null)
                            {
                                _chat.SendMessage(p, $"Ball {ballId}: shot by {ballData.Carrier.Name} (freq {ballData.Freq}) " +
                                    $"from {(char)('A' + x)}{y + 1} ({ballData.X / 16},{ballData.Y / 16})");
                            }
                            else
                            {
                                _chat.SendMessage(p, $"Ball {ballId}: on map (freq {ballData.Freq}) " +
                                    $"{((ballData.XSpeed != 0 || ballData.YSpeed != 0) ? "last seen" : "still")} " +
                                    $"at {(char)('A' + x)}{y + 1} ({ballData.X / 16},{ballData.Y / 16})");
                            }
                            break;

                        case BallState.Carried:
                            _chat.SendMessage(p, $"Ball {ballId}: carried by {ballData.Carrier.Name} (freq {ballData.Freq}) " +
                                $"at {(char)('A' + x)}{y + 1} ({ballData.Carrier.Position.X / 16},{ballData.Carrier.Position.Y / 16})");
                            break;

                        case BallState.Waiting:
                            _chat.SendMessage(p, $"Ball {ballId}: waiting to be respawned.");
                            break;
                    }
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None, 
            Description = "Displays information about server internals. It currently includes information about object pooling.")]
        private void Command_serverstats(string command, string parameters, Player p, ITarget target)
        {
            IObjectPoolManager poolManager = _broker.GetInterface<IObjectPoolManager>();
            if (poolManager != null)
            {
                try
                {
                    _chat.SendMessage(p, "Object Pooling Statistics (available/total):");

                    foreach (var pool in poolManager.Pools)
                    {
                        _chat.SendMessage(p, $"{pool.Type} ({pool.ObjectsAvailable}/{pool.ObjectsCreated})");
                    }
                }
                finally
                {
                    _broker.ReleaseInterface(ref poolManager);
                }
            }
        }
    }
}

using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Core.Map;
using SS.Packets;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using static SS.Core.ComponentInterfaces.IPeer;

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

        // Regular dependencies (do not add any of these to a command group)
        private IChat _chat;
        private ICommandManager _commandManager;
        private ILogManager _logManager;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;

        // Command group dependencies (these are set using reflection)
        private IArenaManager _arenaManager;
        private IArenaPlayerStats _arenaPlayerStats;
        private IBalls _balls;
        private ICapabilityManager _capabilityManager;
        private IConfigManager _configManager;
        private IFileTransfer _fileTransfer;
        private IGame _game;
        private IGroupManager _groupManager;
        private IJackpot _jackpot;
        private ILagQuery _lagQuery;
        private IMainloop _mainloop;
        private IMapData _mapData;
        private IModuleManager _mm;
        private INetwork _network;
        private IPersistExecutor _persistExecutor;
        private IScoreStats _scoreStats;

        private DateTime _startedAt;

        private readonly Dictionary<Type, InterfaceFieldInfo> _interfaceFields = new();
        private readonly Trie<CommandGroup> _commandGroups = new(false);

        private readonly ObjectPool<ArenaListItem> _arenaListItemPool = new NonTransientObjectPool<ArenaListItem>(new ArenaListItemPooledObjectPolicy());
        private readonly ObjectPool<List<ArenaListItem>> _arenaListItemListPool = new DefaultObjectPool<List<ArenaListItem>>(new ArenaListItemListPooledObjectPolicy());

        public PlayerCommand()
        {
            foreach(FieldInfo fieldInfo in typeof(PlayerCommand).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (fieldInfo.FieldType.IsInterface
                    && fieldInfo.FieldType.IsAssignableTo(typeof(IComponentInterface))
                    && fieldInfo.Name.StartsWith('_'))
                {
                    _interfaceFields.Add(fieldInfo.FieldType, new InterfaceFieldInfo(fieldInfo));
                }
            }

            AddCommandGroup(
                new CommandGroup("core")
                {
                    InterfaceDependencies = new()
                    {
                        typeof(IArenaManager),
                        typeof(INetwork),
                        typeof(IMainloop),
                        typeof(IModuleManager),
                    },
                    Commands = new[]
                    {
                        new CommandInfo("arena", Command_arena),
                        new CommandInfo("shutdown", Command_shutdown),
                        new CommandInfo("recyclezone", Command_recyclezone),
                        new CommandInfo("recyclearena", Command_recyclearena),
                        new CommandInfo("owner", Command_owner),
                        new CommandInfo("zone", Command_zone),
                        new CommandInfo("uptime", Command_uptime),
                        new CommandInfo("version", Command_version),
                        new CommandInfo("lsmod", Command_lsmod),
                        new CommandInfo("modinfo", Command_modinfo),
                        new CommandInfo("insmod", Command_insmod),
                        new CommandInfo("rmmod", Command_rmmod),
                        new CommandInfo("attmod", Command_attmod),
                        new CommandInfo("detmod", Command_detmod),
                        new CommandInfo("info", Command_info),
                        new CommandInfo("a", Command_a),
                        new CommandInfo("aa", Command_aa),
                        new CommandInfo("z", Command_z),
                        new CommandInfo("az", Command_az),
                        new CommandInfo("warn", Command_warn),
                        new CommandInfo("reply", Command_reply),
                        new CommandInfo("netstats", Command_netstats),
                        new CommandInfo("serverstats", Command_serverstats),
                        new CommandInfo("send", Command_send),
                        new CommandInfo("where", Command_where)
                    }
                });

            AddCommandGroup(
                new CommandGroup("game")
                {
                    InterfaceDependencies = new()
                    {
                        typeof(IArenaManager),
                        typeof(IConfigManager),
                        typeof(IGame),
                        typeof(INetwork),
                    },
                    Commands = new[]
                    {
                        new CommandInfo("setfreq", Command_setfreq),
                        new CommandInfo("setship", Command_setship),
                        new CommandInfo("specall", Command_specall),
                        new CommandInfo("warpto", Command_warpto),
                        new CommandInfo("shipreset", Command_shipreset),
                        new CommandInfo("prize", Command_prize),
                        new CommandInfo("lock", Command_lock),
                        new CommandInfo("unlock", Command_unlock),
                        new CommandInfo("lockarena", Command_lockarena),
                        new CommandInfo("unlockarena", Command_unlockarena),
                    }
                });

            AddCommandGroup(
                new CommandGroup("jackpot")
                {
                    InterfaceDependencies = new()
                    {
                        typeof(IArenaManager),
                        typeof(ICapabilityManager),
                        typeof(IJackpot),
                    },
                    Commands = new[]
                    {
                        new CommandInfo("jackpot", Command_jackpot),
                        new CommandInfo("setjackpot", Command_setjackpot),
                    }
                });

            AddCommandGroup(
                new CommandGroup("config")
                {
                    InterfaceDependencies = new()
                    {
                        typeof(IArenaManager),
                        typeof(IConfigManager),
                    },
                    Commands = new[]
                    {
                        new CommandInfo("geta", Command_getX),
                        new CommandInfo("getg", Command_getX),
                        new CommandInfo("seta", Command_setX),
                        new CommandInfo("setg", Command_setX),
                    }
                });

            AddCommandGroup(
                new CommandGroup("flag")
                {
                    InterfaceDependencies = new()
                    {
                    },
                    Commands = new[]
                    {
                        new CommandInfo("flagreset", Command_flagreset),
                    }
                });

            AddCommandGroup(
                new CommandGroup("ball")
                {
                    InterfaceDependencies = new()
                    {
                        typeof(IBalls),
                    },
                    Commands = new[]
                    {
                        new CommandInfo("ballcount", Command_ballcount),
                        new CommandInfo("ballinfo", Command_ballinfo),
                        new CommandInfo("giveball", Command_giveball),
                        new CommandInfo("moveball", Command_moveball),
                        new CommandInfo("spawnball", Command_spawnball),
                    }
                });

            AddCommandGroup(
                new CommandGroup("lag")
                {
                    InterfaceDependencies = new()
                    {
                        typeof(ILagQuery),
                    },
                    Commands = new[]
                    {
                        new CommandInfo("lag", Command_lag),
                    }
                });

            AddCommandGroup(
                new CommandGroup("stats")
                {
                    InterfaceDependencies = new()
                    {
                        typeof(IArenaPlayerStats),
                        typeof(IConfigManager),
                        typeof(IPersistExecutor),
                        typeof(IScoreStats),
                    },
                    Commands = new[]
                    {
                        new CommandInfo("endinterval", Command_endinterval),
                        new CommandInfo("scorereset", Command_scorereset),
                        new CommandInfo("points", Command_points)
                    }
                });

            AddCommandGroup(
                new CommandGroup("misc")
                {
                    InterfaceDependencies = new()
                    {
                        typeof(ICapabilityManager),
                        typeof(IGroupManager),
                        typeof(IArenaManager),
                        typeof(IConfigManager),
                        typeof(IFileTransfer),
                        typeof(IMapData),
                    },
                    Commands = new[]
                    {
                        new CommandInfo("getgroup", Command_getgroup),
                        new CommandInfo("setgroup", Command_setgroup),
                        new CommandInfo("rmgroup", Command_rmgroup),
                        new CommandInfo("grplogin", Command_grplogin),
                        new CommandInfo("listmod", Command_listmod),
                        new CommandInfo("find", Command_find),
                        new CommandInfo("setcm", Command_setcm),
                        new CommandInfo("getcm", Command_getcm),
                        new CommandInfo("listarena", Command_listarena),
                        new CommandInfo("sheep", Command_sheep),
                        new CommandInfo("mapinfo", Command_mapinfo),
                        new CommandInfo("mapimage", Command_mapimage),
                    }
                });

            void AddCommandGroup(CommandGroup group)
            {
                if (group == null)
                    throw new ArgumentNullException(nameof(group));

                _commandGroups.Add(group.Name, group);
            }
        }

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IChat chat,
            ICommandManager commandManager,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            // Setting the command group dependencies to null to remove the warnings.
            // These will actually get set via reflection when the command groups are loaded.
            _arenaManager = null;
            _arenaPlayerStats = null;
            _balls = null;
            _capabilityManager = null;
            _configManager = null;
            _fileTransfer = null;
            _game = null;
            _groupManager = null;
            _jackpot = null;
            _lagQuery = null;
            _mainloop = null;
            _mapData = null;
            _mm = null;
            _network = null;
            _persistExecutor = null;
            _scoreStats = null;

            _startedAt = DateTime.UtcNow;

            foreach (CommandGroup group in _commandGroups.Values)
            {
                LoadCommandGroup(group);
            }

            // These commands are purposely not in command groups.
            // We want these commands available as long as the module is loaded.
            _commandManager.AddCommand("enablecmdgroup", Command_enablecmdgroup);
            _commandManager.AddCommand("disablecmdgroup", Command_disablecmdgroup);

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            foreach (CommandGroup group in _commandGroups.Values)
            {
                UnloadCommandGroup(group);
            }

            _commandManager.RemoveCommand("enablecmdgroup", Command_enablecmdgroup);
            _commandManager.RemoveCommand("disablecmdgroup", Command_disablecmdgroup);

            return true;
        }

        #endregion

        private bool LoadCommandGroup(CommandGroup group)
        {
            if (group == null)
                throw new ArgumentNullException(nameof(group));

            if (group.IsLoaded)
                return false;

            foreach (Type dependencyType in group.InterfaceDependencies)
            {
                if (!_interfaceFields.TryGetValue(dependencyType, out InterfaceFieldInfo interfaceFieldInfo))
                {
                    _logManager.LogM(LogLevel.Error, nameof(PlayerCommand), $"Failed to load command group '{group.Name}'. Error getting interface dependency '{dependencyType.Name}' field info.");
                    return false;
                }

                if (interfaceFieldInfo.ReferenceCount == 0)
                {
                    IComponentInterface componentInterface = _broker.GetInterface(dependencyType);
                    if (componentInterface == null)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(PlayerCommand), $"Failed to load command group '{group.Name}'. Error getting interface dependency '{dependencyType.Name}' from broker.");
                        return false;
                    }

                    interfaceFieldInfo.FieldInfo.SetValue(this, componentInterface);
                }

                interfaceFieldInfo.ReferenceCount++;
            }

            foreach (CommandInfo commandInfo in group.Commands)
            {
                if (commandInfo.CommandDelegate != null)
                    _commandManager.AddCommand(commandInfo.CommandName, commandInfo.CommandDelegate);
                else if (commandInfo.CommandWithSoundDelegate != null)
                    _commandManager.AddCommand(commandInfo.CommandName, commandInfo.CommandWithSoundDelegate);
            }

            group.IsLoaded = true;
            return true;
        }

        private bool UnloadCommandGroup(CommandGroup group)
        {
            if (group == null)
                throw new ArgumentNullException(nameof(group));

            if (!group.IsLoaded)
                return false;

            foreach (CommandInfo commandInfo in group.Commands)
            {
                if (commandInfo.CommandDelegate != null)
                    _commandManager.RemoveCommand(commandInfo.CommandName, commandInfo.CommandDelegate);
                else if (commandInfo.CommandWithSoundDelegate != null)
                    _commandManager.RemoveCommand(commandInfo.CommandName, commandInfo.CommandWithSoundDelegate);
            }

            foreach (Type dependencyType in group.InterfaceDependencies)
            {
                if (!_interfaceFields.TryGetValue(dependencyType, out InterfaceFieldInfo interfaceFieldInfo))
                {
                    _logManager.LogM(LogLevel.Error, nameof(PlayerCommand), $"Error unloading command group {group.Name}. Error getting interface field info for '{dependencyType.Name}'.");
                    return false;
                }

                interfaceFieldInfo.ReferenceCount--;

                if (interfaceFieldInfo.ReferenceCount == 0)
                {
                    if (interfaceFieldInfo.FieldInfo.GetValue(this) is not IComponentInterface componentInterface)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(PlayerCommand), $"Error unloading command group {group.Name}. Error getting interface field value for '{dependencyType.Name}'.");
                        return false;
                    }

                    _broker.ReleaseInterface(dependencyType, componentInterface);

                    interfaceFieldInfo.FieldInfo.SetValue(this, null);
                }
            }

            group.IsLoaded = false;
            return true;
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-all} | <command group>]",
            Description = "Enables all the commands in the specified command group. This is only\n" +
            "useful after using ?disablecmdgroup. Use {-all} to enable all command groups.")]
        private void Command_enablecmdgroup(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (parameters.IsWhiteSpace())
            {
                PrintCommandGroups(player);
                return;
            }

            if (parameters.Contains("-all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (CommandGroup group in _commandGroups.Values)
                {
                    LoadGroup(group);
                }
            }
            else
            {
                if (!_commandGroups.TryGetValue(parameters, out CommandGroup group))
                {
                    _chat.SendMessage(player, $"Command group '{parameters}' not found.");
                    return;
                }

                LoadGroup(group);
            }

            void LoadGroup(CommandGroup group)
            {
                if (group.IsLoaded)
                    _chat.SendMessage(player, $"Command group '{group.Name}' is already enabled.");
                else if (LoadCommandGroup(group))
                    _chat.SendMessage(player, $"Command group '{group.Name}' enabled.");
                else
                    _chat.SendMessage(player, $"Error enabling command group '{group.Name}'.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-all} | <command group>]",
            Description = "Disables all the commands in the specified command group and released the\n" +
            "modules that they require. This can be used to release interfaces so that\n" +
            $"modules can be unloaded or upgraded without unloading the {nameof(PlayerCommand)}\n" +
            "module which would be irreversible). Use {-all} to disable all command groups.")]
        private void Command_disablecmdgroup(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (parameters.IsWhiteSpace())
            {
                PrintCommandGroups(player);
                return;
            }

            if (parameters.Contains("-all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (CommandGroup group in _commandGroups.Values)
                {
                    UnloadGroup(group);
                }
            }
            else
            {
                if (!_commandGroups.TryGetValue(parameters, out CommandGroup group))
                {
                    _chat.SendMessage(player, $"Command group '{parameters}' not found.");
                    return;
                }

                UnloadGroup(group);
            }

            void UnloadGroup(CommandGroup group)
            {
                if (!group.IsLoaded)
                    _chat.SendMessage(player, $"Command group '{group.Name}' is already disabled.");
                else if (UnloadCommandGroup(group))
                    _chat.SendMessage(player, $"Command group '{group.Name}' disabled.");
                else
                    _chat.SendMessage(player, $"Error disabling command group '{group.Name}'.");
            }
        }

        private void PrintCommandGroups(Player player)
        {
            if (player == null)
                return;

            _chat.SendMessage(player, "Available command groups:");

            foreach (CommandGroup group in _commandGroups.Values)
            {
                _chat.SendMessage(player, $"{group.Name,10} : {(group.IsLoaded ? "enabled" : "disabled")}");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = "[{-v}]",
            Description =
            "Displays lag information about you or a target player.\n" +
            "Use {-v} for more detail. The format of the ping fields is\n" +
            "\"last average (min-max)\".")]
        private void Command_lag(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
                targetPlayer = player;

            if (!targetPlayer.IsStandard)
            {
                _chat.SendMessage(player, $"{(targetPlayer == player ? "You" : targetPlayer.Name)} {(targetPlayer == player ? "aren't" : "isn't")} a game client.");
                return;
            }

            _lagQuery.QueryPositionPing(targetPlayer, out PingSummary positionPing);
            _lagQuery.QueryClientPing(targetPlayer, out ClientPingSummary clientPing);
            _lagQuery.QueryReliablePing(targetPlayer, out PingSummary reliablePing);
            _lagQuery.QueryPacketloss(targetPlayer, out PacketlossSummary packetloss);

            // weight reliable ping twice the S2C and C2S
            int average = (positionPing.Average + clientPing.Average + 2 * reliablePing.Average) / 4;

            string prefix = targetPlayer == player ? "lag" : targetPlayer.Name;

            if (!parameters.Contains("-v", StringComparison.OrdinalIgnoreCase))
            {
                _chat.SendMessage(player, $"{prefix}: avg ping: {average} ploss: s2c: {packetloss.s2c * 100d:F2} c2s: {packetloss.c2s * 100d:F2}");
            }
            else
            {
                _lagQuery.QueryReliableLag(targetPlayer, out ReliableLagData reliableLag);

                _chat.SendMessage(player, $"{prefix}: s2c ping: {clientPing.Current} {clientPing.Average} ({clientPing.Min}-{clientPing.Max}) (reported by client)");
                _chat.SendMessage(player, $"{prefix}: c2s ping: {positionPing.Current} {positionPing.Average} ({positionPing.Min}-{positionPing.Max}) (from position pkt times)");
                _chat.SendMessage(player, $"{prefix}: rel ping: {reliablePing.Current} {reliablePing.Average} ({reliablePing.Min}-{reliablePing.Max}) (reliable ping)");
                _chat.SendMessage(player, $"{prefix}: effective ping: {average} (average of above)");

                _chat.SendMessage(player, $"{prefix}: ploss: s2c: {packetloss.s2c * 100d:F2} c2s: {packetloss.c2s * 100d:F2} s2cwpn: {packetloss.s2cwpn * 100:F2}");
                _chat.SendMessage(player, $"{prefix}: reliable dups: {reliableLag.RelDups * 100d / reliableLag.C2SN:F2}% reliable resends: {reliableLag.Retries * 100d / reliableLag.S2CN:F2}%");
                _chat.SendMessage(player, $"{prefix}: s2c slow: {clientPing.S2CSlowCurrent}/{clientPing.S2CSlowTotal} s2c fast: {clientPing.S2CFastCurrent}/{clientPing.S2CFastTotal}");

                PrintCommonBandwidthInfo(player, targetPlayer, DateTime.UtcNow - targetPlayer.ConnectTime, prefix, false);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[[-g] | [-a <arena group name>]] <interval name>",
            Description =
            "Causes the specified interval to be reset. If {-g} is specified, reset the interval\n" +
            "at the global scope. If {-a} is specified, use the named arena group. Otherwise, use\n" +
            "the current arena's scope. Interval names can be \"game\", \"reset\", or \"maprotation\".")]
        private void Command_endinterval(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token;
            bool dashA = false;
            ReadOnlySpan<char> arenaGroup = ReadOnlySpan<char>.Empty;
            PersistInterval? interval = null;

            while ((token = remaining.GetToken(" \t", out remaining)).Length > 0)
            {
                if (dashA)
                {
                    if (token.StartsWith("-"))
                    {
                        _chat.SendMessage(player, "Invalid arena group name.");
                        return;
                    }

                    arenaGroup = token;
                    dashA = false;
                }
                else if (token.Equals("-g", StringComparison.Ordinal))
                {
                    if (!arenaGroup.IsWhiteSpace())
                    {
                        _chat.SendMessage(player, "The -g option cannot be used with -a, and it can only appear once.");
                        return;
                    }

                    arenaGroup = Constants.ArenaGroup_Global;
                }
                else if (token.Equals("-a", StringComparison.Ordinal))
                {
                    if (!arenaGroup.IsWhiteSpace())
                    {

                        _chat.SendMessage(player, "The -a option cannot be used with -g, and it can only appear once.");
                        return;
                    }

                    dashA = true;
                }
                else
                {
                    PersistInterval tempInterval;

                    if (token.Equals("game", StringComparison.OrdinalIgnoreCase))
                        tempInterval = PersistInterval.Game;
                    else if (token.Equals("reset", StringComparison.OrdinalIgnoreCase))
                        tempInterval = PersistInterval.Reset;
                    else if (token.Equals("maprotation", StringComparison.OrdinalIgnoreCase))
                        tempInterval = PersistInterval.MapRotation;
                    else
                    {
                        _chat.SendMessage(player, $"Bad argument: {token}");
                        return;
                    }

                    if (interval != null)
                    {
                        _chat.SendMessage(player, "The -a option cannot be used with -g, and it can only appear once.");
                        return;
                    }

                    interval = tempInterval;
                }
            }

            if (dashA)
            {
                _chat.SendMessage(player, $"An arena group must be specified after -a.");
                return;
            }

            if (interval == null)
            {
                _chat.SendMessage(player, $"An interval must be speciifed.");
                return;
            }

            if (!arenaGroup.IsEmpty)
            {
                _persistExecutor.EndInterval(interval.Value, arenaGroup.ToString());
            }
            else if (player.Arena != null)
            {
                _persistExecutor.EndInterval(interval.Value, player.Arena);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None | CommandTarget.Player,
            Args = null,
            Description = "Resets your own score, or the target player's score.")]
        private void Command_scorereset(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            // For now, only reset PersistInterval.Reset scores, since those are the only ones the client sees.
            if (target.TryGetArenaTarget(out Arena arena))
            {
                if (_configManager.GetInt(arena.Cfg, "Misc", "SelfScoreReset", 0) != 0)
                {
                    _scoreStats.ScoreReset(player, PersistInterval.Reset);
                    _scoreStats.SendUpdates(arena, null);
                    _chat.SendMessage(player, $"Your score has been reset.");
                }
                else
                {
                    _chat.SendMessage(player, $"This arena doesn't allow you to reset your own scores.");
                }
            }
            else if (target.TryGetPlayerTarget(out Player otherPlayer))
            {
                arena = otherPlayer.Arena;

                if (arena != null)
                {
                    _scoreStats.ScoreReset(otherPlayer, PersistInterval.Reset);
                    _scoreStats.SendUpdates(arena, null);
                    _chat.SendMessage(player, $"Player {otherPlayer.Name} has had their score reset.");
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Any,
            Args = "<points to add>",
            Description = "Adds the specified number of points to the targets' flag points.")]
        private void Command_points(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!int.TryParse(parameters, out int pointsToAdd))
                return;

            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
            HashSet<Arena> arenas = _objectPoolManager.ArenaSetPool.Get();

            try
            {
                _playerData.TargetToSet(target, players);

                foreach (Player targetPlayer in players)
                {
                    _arenaPlayerStats.IncrementStat(targetPlayer, StatCodes.FlagPoints, null, (ulong)pointsToAdd);

                    if (targetPlayer.Arena is not null)
                    {
                        arenas.Add(targetPlayer.Arena);
                    }
                }

                foreach (Arena arena in arenas)
                {
                    _scoreStats.SendUpdates(arena, null);
                }
            }
            finally
            {
                _objectPoolManager.ArenaSetPool.Return(arenas);
                _objectPoolManager.PlayerSetPool.Return(players);
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
        private void Command_arena(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            bool isChatOutput = player.Type == ClientType.Chat || parameters.Contains("-t", StringComparison.OrdinalIgnoreCase);
            bool includePrivateArenas = _capabilityManager.HasCapability(player, Constants.Capabilities.SeePrivArena);
            bool showAllPeer = parameters.Contains("-p", StringComparison.OrdinalIgnoreCase);

            List<ArenaListItem> arenaList = isChatOutput ? _arenaListItemListPool.Get() : null;
            Span<byte> bufferSpan = isChatOutput ? Span<byte>.Empty : stackalloc byte[1024];

            if (!isChatOutput)
            {
                // Write header
                bufferSpan[0] = (byte)S2CPacketType.Arena;
            }

            int length = 1;

            _arenaManager.Lock();

            try
            {
                // refresh arena counts
                _arenaManager.GetPopulationSummary(out _, out _);

                foreach (Arena arena in _arenaManager.Arenas)
                {
                    if (arena.Status == ArenaState.Running
                        && (!arena.IsPrivate || includePrivateArenas || player.Arena == arena))
                    {
                        if (isChatOutput)
                        {
                            ArenaListItem item = _arenaListItemPool.Get();
                            item.Set(arena.Name, arena.Total, arena == player.Arena);
                            arenaList.Add(item);
                        }
                        else
                        {
                            if (!AppendToPacket(bufferSpan, ref length, arena.Name, arena.Total, arena == player.Arena))
                                break;
                        }

                    }
                }
            }
            finally
            {
                _arenaManager.Unlock();
            }

            IPeer peer = _broker.GetInterface<IPeer>();
            if (peer is not null)
            {
                peer.Lock();
                try
                {
                    // Note: Purposely using indexing to access the lists. Enumerating would incur allocations.

                    for (int zoneIndex = 0; zoneIndex < peer.Peers.Count; zoneIndex++)
                    {
                        IPeerZone peerZone = peer.Peers[zoneIndex];
                        for (int arenaIndex = 0; arenaIndex < peerZone.Arenas.Count; arenaIndex++)
                        {
                            IPeerArena peerArena = peerZone.Arenas[arenaIndex];
                            if (!peerArena.IsConfigured && !showAllPeer)
                                continue;

                            if (isChatOutput)
                            {
                                ArenaListItem item = _arenaListItemPool.Get();
                                item.Set(peerArena.Name.LocalName, peerArena.PlayerCount, false);
                                arenaList.Add(item);
                            }
                            else
                            {
                                if (!AppendToPacket(bufferSpan, ref length, peerArena.Name.LocalName, peerArena.PlayerCount, false))
                                    break;
                            }
                        }
                    }
                }
                finally
                {
                    peer.Unlock();
                    _broker.ReleaseInterface(ref peer);
                }
            }

            if (parameters.Contains("-a", StringComparison.OrdinalIgnoreCase))
            {
                // TODO: additional arena list logic (-a argument)
            }

            if (isChatOutput)
            {
                arenaList.Sort(ArenaListItemComparer.Instance);

                foreach (ArenaListItem item in arenaList)
                {
                    _chat.SendMessage(player, $"{item.ArenaName,-15} {item.PlayerCount,3}{(item.IsCurrent ? " (current)" : "")}");
                    _arenaListItemPool.Return(item);
                }

                _arenaListItemListPool.Return(arenaList);
            }
            else
            {
                _network.SendToOne(player, bufferSpan[..length], NetSendFlags.Reliable);
            }

            static bool AppendToPacket(Span<byte> bufferSpan, ref int length, ReadOnlySpan<char> arenaName, int playerCount, bool isCurrent)
            {
                int nameLength = StringUtils.DefaultEncoding.GetByteCount(arenaName) + 1; // +1 because the name in the packet is null terminated
                int additionalLength = nameLength + 2;

                if (length + additionalLength > (Constants.MaxPacket - ReliableHeader.Length))
                {
                    return false;
                }

                // arena name
                Span<byte> remainingSpan = bufferSpan[length..];
                remainingSpan = remainingSpan[remainingSpan.WriteNullTerminatedString(arenaName)..];

                // player count (a negative value denotes the player's current arena)
                Span<byte> playerCountSpan = remainingSpan[..2];
                BinaryPrimitives.WriteInt16LittleEndian(playerCountSpan, isCurrent ? (short)-playerCount : (short)playerCount);

                length += additionalLength;
                return true;
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-r}]",
            Description =
            "Immediately shuts down the server, exiting with {EXIT_NONE}. If\n" +
            "{-r} is specified, exit with {EXIT_RECYCLE} instead. The {run-asss}\n" +
            "script, if it is being used, will notice {EXIT_RECYCLE} and restart\n" +
            "the server.")]
        private void Command_shutdown(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ExitCode code = parameters.Equals("-r", StringComparison.OrdinalIgnoreCase)
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
        private void Command_recyclezone(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _mainloop.Quit(ExitCode.Recycle);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Recycles the current arena without kicking players off.")]
        private void Command_recyclearena(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!_arenaManager.RecycleArena(player.Arena))
            {
                _chat.SendMessage(player, "Arena recycle failed; check the log for details.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None, 
            Args = null, 
            Description = "Displays the arena owner.")]
        private void Command_owner(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena arena = player.Arena;
            if (arena == null)
                return;

            string ownerName = _configManager.GetStr(arena.Cfg, "Owner", "Name");

            if (!string.IsNullOrWhiteSpace(ownerName))
            {
                _chat.SendMessage(player, $"This arena is owned by {ownerName}.");
            }
            else
            {
                _chat.SendMessage(player, $"This arena has no listed owner.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Displays the name of the zone.")]
        private void Command_zone(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            string zoneName = _configManager.GetStr(_configManager.Global, "Billing", "ServerName");
            _chat.SendMessage(player, $"Zone: {(!string.IsNullOrWhiteSpace(zoneName) ? zoneName : "(none)")}");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Displays how long the server has been running.")]
        private void Command_uptime(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            TimeSpan ts = DateTime.UtcNow - _startedAt;

            _chat.SendMessage(player, $"uptime: {ts.Days} days {ts.Hours} hours {ts.Minutes} minutes {ts.Seconds} seconds");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-v}]",
            Description =
            "Prints out information about the server.\n" +
            "For staff members, it will print more detailed version information.\n" +
            "If staff members specify the {-v} arg, it will print even more verbose information.")]
        private void Command_version(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _chat.SendMessage(player, $"Subspace Server .NET");

            if (_capabilityManager.HasCapability(player, Constants.Capabilities.IsStaff))
            {
                _chat.SendMessage(player, $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                    $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} " +
                    $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!parameters.Contains("v", StringComparison.OrdinalIgnoreCase)
                        && (assembly.FullName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                            || assembly.FullName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                            || assembly.FullName.StartsWith("netstandard,", StringComparison.OrdinalIgnoreCase)
                        ))
                    {
                        continue;
                    }

                    _chat.SendMessage(player, $"{assembly.FullName}");
                }
            }
        }

        private void Command_sheep(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (target.Type != TargetType.Arena)
                return;

            string sheepMessage = _configManager.GetStr(player.Arena.Cfg, "Misc", "SheepMessage");

            if (sheepMessage != null)
                _chat.SendMessage(player, ChatSound.Sheep, sheepMessage);
            else
                _chat.SendMessage(player, ChatSound.Sheep, "Sheep successfully cloned -- hello Dolly");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "none or <arena name> or all",
            Description = "Displays the current jackpot for this arena, the named arena, or all arenas.")]
        private void Command_jackpot(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (parameters.IsWhiteSpace())
            {
                _chat.SendMessage(player, $"The jackpot is {_jackpot.GetJackpot(player.Arena)}.");
                return;
            }

            if (parameters.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                bool canSeePrivArena = _capabilityManager.HasCapability(player, Constants.Capabilities.SeePrivArena);

                _arenaManager.Lock();

                try
                {
                    foreach (Arena arena in _arenaManager.Arenas)
                    {
                        int jackpotValue;
                        if (arena.Status == ArenaState.Running
                            && (!arena.IsPrivate || canSeePrivArena || player.Arena == arena)
                            && (jackpotValue = _jackpot.GetJackpot(arena)) > 0)
                        {
                            _chat.SendMessage(player, $"The jackpot in {arena.Name} is {jackpotValue}.");
                        }
                    }
                }
                finally
                {
                    _arenaManager.Unlock();
                }
            }
            else
            {
                Arena arena = _arenaManager.FindArena(parameters);
                if (arena != null)
                {
                    _chat.SendMessage(player, $"The jackpot in {arena.Name} is {_jackpot.GetJackpot(arena)}.");
                }
                else
                {
                    _chat.SendMessage(player, $"Arena '{parameters}' does not exist.");
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<new jackpot value>",
            Description = "Sets the jackpot for this arena to a new value.")]
        private void Command_setjackpot(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!int.TryParse(parameters, out int value))
            {
                _chat.SendMessage(player, $"setjackpot: bad value");
                return;
            }

            _jackpot.SetJackpot(player.Arena, value);
            _chat.SendMessage(player, $"The jackpot is {_jackpot.GetJackpot(player.Arena)}.");
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
        private void Command_getX(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token = remaining.GetToken(':', out remaining).Trim();
            if (token.IsWhiteSpace() || (remaining = remaining.TrimStart(':').Trim()).IsWhiteSpace())
            {
                _chat.SendMessage(player, $"Usage: ?{command} <section>:<key>");
                return;
            }

            ReadOnlySpan<char> section = token;
            ReadOnlySpan<char> key = remaining;

            ConfigHandle ch = command.Equals("geta", StringComparison.OrdinalIgnoreCase) ? player.Arena.Cfg : _configManager.Global;
            string result = _configManager.GetStr(ch, section, key);
            if (result != null)
            {
                _chat.SendMessage(player, $"{section}:{key} = {result}");
            }
            else
            {
                _chat.SendMessage(player, $"{section}:{key} not found.");
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
        private void Command_setX(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (parameters.IsWhiteSpace())
                return;

            ConfigHandle ch = command.Equals("seta", StringComparison.OrdinalIgnoreCase) ? player.Arena.Cfg : _configManager.Global;
            bool permanent = true;

            ReadOnlySpan<char> line = parameters;
            if (line.StartsWith("-t"))
            {
                permanent = false;
                line = line[2..];
            }

            Configuration.ConfFile.ParseConfProperty(line, out string section, out string key, out ReadOnlySpan<char> value, out _);
            _configManager.SetStr(ch, section, key, value.ToString(), $"Set by {player.Name} on {DateTime.Now}", permanent);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Prints out some statistics from the network layer.")]
        private void Command_netstats(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ulong secs = Convert.ToUInt64((DateTime.UtcNow - _startedAt).TotalSeconds);

            IReadOnlyNetStats stats = _network.GetStats();
            _chat.SendMessage(player, $"netstats: pings={stats.PingsReceived}  pkts sent={stats.PacketsSent}  pkts recvd={stats.PacketsReceived}");

            // IP Header (20 bytes) + UDP Header (8 bytes) = 28 bytes total overhead for each packet
            ulong bwout = (stats.BytesSent + stats.PacketsSent * 28) / secs;
            ulong bwin = (stats.BytesReceived + stats.PacketsReceived * 28) / secs;
            _chat.SendMessage(player, $"netstats: bw out={bwout}  bw in={bwin}");

            _chat.SendMessage(player, $"netstats: buffers used={stats.BuffersUsed}/{stats.BuffersTotal} ({(double)stats.BuffersUsed / stats.BuffersTotal:p})");

            _chat.SendMessage(player, $"netstats: grouped=" +
                $"{stats.GroupedStats0}/" +
                $"{stats.GroupedStats1}/" +
                $"{stats.GroupedStats2}/" +
                $"{stats.GroupedStats3}/" +
                $"{stats.GroupedStats4}/" +
                $"{stats.GroupedStats5}/" +
                $"{stats.GroupedStats6}/" +
                $"{stats.GroupedStats7}");

            _chat.SendMessage(player, $"netstats: rel grouped=" +
                $"{stats.RelGroupedStats0}/" +
                $"{stats.RelGroupedStats1}/" +
                $"{stats.RelGroupedStats2}/" +
                $"{stats.RelGroupedStats3}/" +
                $"{stats.RelGroupedStats4}/" +
                $"{stats.RelGroupedStats5}/" +
                $"{stats.RelGroupedStats6}/" +
                $"{stats.RelGroupedStats7}");

            _chat.SendMessage(player, $"netstats: pri=" +
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
        private void Command_info(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
            {
                _chat.SendMessage(player, "info: must use on a player");
                return;
            }

            string prefix = targetPlayer.Name;
            TimeSpan connectedTimeSpan = DateTime.UtcNow - targetPlayer.ConnectTime;

            _chat.SendMessage(player, $"{prefix}: pid={targetPlayer.Id} name='{targetPlayer.Name}' squad='{targetPlayer.Squad}' " +
                $"auth={(targetPlayer.Flags.Authenticated ? 'Y' : 'N')} ship={targetPlayer.Ship} freq={targetPlayer.Freq}");
            _chat.SendMessage(player, $"{prefix}: arena={(targetPlayer.Arena != null ? targetPlayer.Arena.Name : "(none)")} " +
                $"client={targetPlayer.ClientName} res={targetPlayer.Xres}x{targetPlayer.Yres} onFor={connectedTimeSpan} " +
                $"connectAs={(!string.IsNullOrWhiteSpace(targetPlayer.ConnectAs) ? targetPlayer.ConnectAs : "<default>")}");

            if (targetPlayer.IsStandard)
            {
                PrintCommonBandwidthInfo(player, targetPlayer, connectedTimeSpan, prefix, true);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = "<text>",
            Description = "Displays the text as an arena (green) message to the targets.")]
        private void Command_a(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target, ChatSound sound)
        {
            if (target.TryGetArenaTarget(out Arena arena))
            {
                // For arena, using SendArenaMessage instead of SendSetMessage so that the ChatMessageCallback can be fired.
                _chat.SendArenaMessage(arena, sound, $"{parameters} -{player.Name}");
            }
            else
            {
                HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    _playerData.TargetToSet(target, set);
                    _chat.SendSetMessage(set, sound, $"{parameters} -{player.Name}");
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(set);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = "<text>",
            Description = "Displays the text as an anonymous arena (green) message to the targets.")]
        private void Command_aa(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target, ChatSound sound)
        {
            if (target.TryGetArenaTarget(out Arena arena))
            {
                // For arena, using SendArenaMessage instead of SendSetMessage so that the ChatMessageCallback can be fired.
                _chat.SendArenaMessage(arena, sound, parameters);
            }
            else
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
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<text>",
            Description = "Displays the text as an arena (green) message to the whole zone.")]
        private void Command_z(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target, ChatSound sound)
        {
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                sb.Append(parameters);
                sb.Append(" -");
                sb.Append(player.Name);

                _chat.SendArenaMessage(null, sound, sb);

                IPeer peer = _broker.GetInterface<IPeer>();
                if (peer is not null)
                {
                    try
                    {
                        peer.SendZoneMessage(sb);
                    }
                    finally
                    {
                        _broker.ReleaseInterface(ref peer);
                    }
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<text>",
            Description = "Displays the text as an anonymous arena (green) message to the whole zone.")]
        private void Command_az(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target, ChatSound sound)
        {
            _chat.SendArenaMessage(null, sound, parameters);

            IPeer peer = _broker.GetInterface<IPeer>();
            if (peer is not null)
            {
                try
                {
                    peer.SendZoneMessage(parameters);
                }
                finally
                {
                    _broker.ReleaseInterface(ref peer);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = "<message>",
            Description = "Sends a red warning message to a player.")]
        private void Command_warn(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
            {
                _chat.SendMessage(player, "You must target a player.");
                return;
            }

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                set.Add(targetPlayer);

                if (_capabilityManager.HasCapability(player, Constants.Capabilities.IsStaff))
                {
                    _chat.SendAnyMessage(set, ChatMessageType.SysopWarning, ChatSound.Beep1, null, $"WARNING: {parameters} -{player.Name}");
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

            _chat.SendMessage(player, $"Player '{targetPlayer.Name}' has been warned.");
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = "<message>",
            Description = "Sends a private message to a player.\n" +
            "Useful for logging replies to moderator help requests.")]
        private void Command_reply(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
            {
                _chat.SendMessage(player, "You must target a player.");
                return;
            }

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                set.Add(targetPlayer);
                _chat.SendAnyMessage(set, ChatMessageType.Private, ChatSound.None, player, parameters);
                _chat.SendMessage(player, $"Private message sent to player '{targetPlayer.Name}'.");
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        private void PrintCommonBandwidthInfo(Player player, Player targetPlayer, TimeSpan connectedTimeSpan, string prefix, bool includeSensitive)
        {
            if (_network == null)
                return;

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                NetClientStats stats = new() { BandwidthLimitInfo = sb };

                _network.GetClientStats(targetPlayer, ref stats);

                if (includeSensitive)
                {
                    _chat.SendMessage(player, $"{prefix}: ip:{stats.IPEndPoint.Address} port:{stats.IPEndPoint.Port} " +
                        $"encName={stats.EncryptionName} macId={targetPlayer.MacId} permId={targetPlayer.PermId}");
                }

                int ignoringwpns = _game != null ? (int)(100f * _game.GetIgnoreWeapons(targetPlayer)) : 0;

                _chat.SendMessage(player,
                    $"{prefix}: " +
                    $"ave bw in/out={(stats.BytesReceived / connectedTimeSpan.TotalSeconds):N0}/{(stats.BytesSent / connectedTimeSpan.TotalSeconds):N0} " +
                    $"ignoringWpns={ignoringwpns}% dropped={stats.PacketsDropped}");

                _chat.SendMessage(player, $"{prefix}: bwlimit={stats.BandwidthLimitInfo}");
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }

            if (targetPlayer.Flags.NoShip)
                _chat.SendMessage(player, $"{prefix}: lag too high to play");

            if (targetPlayer.Flags.NoFlagsBalls)
                _chat.SendMessage(player, $"{prefix}: lag too high to carry flags or balls");
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = "<x xoord> <y coord>",
            Description = "Warps target player(s) to an x,y coordinate.")]
        private void Command_warpto(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (parameters.IsWhiteSpace())
                return;

            ReadOnlySpan<char> coordsSpan = parameters.Trim();

            int index = coordsSpan.IndexOf(' ');
            if (index == -1)
                return;

            if (!short.TryParse(coordsSpan[..index], out short x))
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
        private void Command_shipreset(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _game.ShipReset(target);
        }

        private static readonly (string Name, int Type)[] _prizeLookup = new[]
        {
            ( "random",    0 ),
            ( "charge",   13 ), // must come before "recharge"
            ( "x",         6 ), // must come before "prox"
            ( "recharge",  1 ),
            ( "energy",    2 ),
            ( "rot",       3 ),
            ( "stealth",   4 ),
            ( "cloak",     5 ),
            ( "warp",      7 ),
            ( "gun",       8 ),
            ( "bomb",      9 ),
            ( "bounce",   10 ),
            ( "thrust",   11 ),
            ( "speed",    12 ),
            ( "shutdown", 14 ),
            ( "multi",    15 ),
            ( "prox",     16 ),
            ( "super",    17 ),
            ( "shield",   18 ),
            ( "shrap",    19 ),
            ( "anti",     20 ),
            ( "rep",      21 ),
            ( "burst",    22 ),
            ( "decoy",    23 ),
            ( "thor",     24 ),
            ( "mprize",   25 ),
            ( "brick",    26 ),
            ( "rocket",   27 ),
            ( "port",     28 ),
        };

        private enum ParsePrizeLast
        {
            None,
            Count,
            Word,
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = "see description",
            Description = "Gives the specified prizes to the target player(s).\n" +
            "\n" +
            "Prizes are specified with an optional count, and then a prize name (e.g.\n" +
            "{3 reps}, {anti}). Negative prizes can be specified with a '-' before\n" +
            "the prize name or the count (e.g. {-prox}, {-3 bricks}, {5 -guns}). More\n" +
            "than one prize can be specified in one command. A count without a prize\n" +
            "name means {random}. For compatability, numerical prize ids with {#} are\n" +
            "supported.")]
        private void Command_prize(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            short count = 1;
            ParsePrizeLast last = ParsePrizeLast.None;

            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token;
            while ((token = remaining.GetToken(", ", out remaining)) != ReadOnlySpan<char>.Empty)
            {
                if (short.TryParse(token, out short t))
                {
                    // This is a count.
                    count = t;
                    last = ParsePrizeLast.Count;
                }
                else
                {
                    // Try a word.

                    // Negative prizes are marked with negative counts.
                    if (token[0] == '-' && count > 0)
                    {
                        count = (short)-count;
                        token = token[1..];
                    }

                    // Now try to find the word.
                    int? type = null;

                    if (token[0] == '#'
                        && short.TryParse(token[1..], out t))
                    {
                        type = t;
                    }
                    else
                    {
                        // Try matching using the lookup (the last match wins).
                        foreach (var tuple in _prizeLookup)
                        {
                            if (token.Contains(tuple.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                type = tuple.Type;
                            }
                        }
                    }

                    if (type != null)
                    {
                        // To send a prize, the type can be negative, but the count must be positive.
                        if (count < 0)
                        {
                            if (type > 0)
                                type = -type;

                            count = (short)-count;
                        }

                        _game.GivePrize(target, (Prize)type, count);

                        // Reset count to 1 once we hit a successful word.
                        count = 1;
                    }

                    last = ParsePrizeLast.Word;
                }
            }

            if (last == ParsePrizeLast.Count)
            {
                if (count < 0)
                    count = (short)-count;

                // If the line ends in a count, give that many random prizes.
                _game.GivePrize(target, 0, count); // TODO: investigate why this doesn't work
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = "[-n] [-s] [-t <timeout>]",
            Description = "Locks the specified targets so that they can't change ships. Use ?unlock\n" +
            "to unlock them. By default, ?lock won't change anyone's ship. If {-s} is\n" +
            "present, it will spec the targets before locking them. If {-n} is present,\n" +
            "it will notify players of their change in status. If {-t} is present, you\n" +
            "can specify a timeout in seconds for the lock to be effective.")]
        private void Command_lock(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            int index = parameters.IndexOf("-t", StringComparison.OrdinalIgnoreCase);

            if (index == -1 || !int.TryParse(parameters[(index + 2)..], out int timeout))
                timeout = 0;

            _game.Lock(
                target,
                parameters.Contains("-n", StringComparison.OrdinalIgnoreCase),
                parameters.Contains("-s", StringComparison.OrdinalIgnoreCase),
                timeout);
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = "[-n]",
            Description = "Unlocks the specified targets so that they can now change ships. An optional\n" +
            "{-n} notifies players of their change in status.")]
        private void Command_unlock(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _game.Unlock(target, parameters.Contains("-n", StringComparison.OrdinalIgnoreCase));
        }

        [CommandHelp(
            Targets = CommandTarget.Arena,
            Args = "[-n] [-a] [-i] [-s]",
            Description = "Changes the default locked state for the arena so entering players will be locked\n" +
            "to spectator mode. Also locks everyone currently in the arena to their ships. The {-n}\n" +
            "option means to notify players of their change in status. The {-a} options means to\n" +
            "only change the arena's state, and not lock current players. The {-i} option means to\n" +
            "only lock entering players to their initial ships, instead of spectator mode. The {-s}\n" +
            "means to spec all players before locking the arena.")]
        private void Command_lockarena(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetArenaTarget(out Arena arena))
                return;

            _game.LockArena(
                arena,
                parameters.Contains("-n", StringComparison.OrdinalIgnoreCase),
                parameters.Contains("-a", StringComparison.OrdinalIgnoreCase),
                parameters.Contains("-i", StringComparison.OrdinalIgnoreCase),
                parameters.Contains("-s", StringComparison.OrdinalIgnoreCase));
        }

        [CommandHelp(
            Targets = CommandTarget.Arena,
            Args = "[-n] [-a]",
            Description = "Changes the default locked state for the arena so entering players will not be\n" +
            "locked to spectator mode. Also unlocks everyone currently in the arena to their ships.\n" +
            "The {-n} options means to notify players of their change in status. The {-a} option\n" +
            "means to only change the arena's state, and not unlock current players.")]
        private void Command_unlockarena(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetArenaTarget(out Arena arena))
                return;

            _game.UnlockArena(
                arena,
                parameters.Contains("-n", StringComparison.OrdinalIgnoreCase),
                parameters.Contains("-a", StringComparison.OrdinalIgnoreCase));
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = "[{-f}] <freq number>",
            Description = "Moves the targets to the specified freq.\n" +
            "If -f is specified, this command ignores the arena freqman.")]
        private void Command_setfreq(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            bool useFreqManager = true;
            short? freq = null;

            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token;
            while ((token = remaining.GetToken(' ', out remaining)).Length > 0)
            {
                if (token.Equals("-f", StringComparison.Ordinal))
                {
                    useFreqManager = false;
                }
                else if (freq == null && short.TryParse(token, out short freqNumber))
                {
                    if (freqNumber < 0 || freqNumber > 9999)
                    {
                        _chat.SendMessage(player, $"Invalid freq number.");
                        return;
                    }

                    freq = freqNumber;
                }
            }

            if (freq == null)
            {
                _chat.SendMessage(player, $"You must specify a freq number.");
                return;
            }

            if (useFreqManager && _capabilityManager.HasCapability(player, Constants.Capabilities.ForceShipFreqChange))
            {
                useFreqManager = false;
            }

            IFreqManager freqManager = useFreqManager ? player.Arena.GetInterface<IFreqManager>() : null;
            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                _playerData.TargetToSet(target, players);

                foreach (Player targetPlayer in players)
                {
                    if (freqManager is not null)
                    {
                        freqManager.FreqChange(targetPlayer, freq.Value, sb);

                        if (sb.Length > 0)
                        {
                            _chat.SendMessage(player, $"{targetPlayer.Name}: {sb}");
                            sb.Clear();
                        }
                    }
                    else
                    {
                        _game.SetFreq(targetPlayer, freq.Value);
                    }
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
                _objectPoolManager.PlayerSetPool.Return(players);

                if (freqManager is not null)
                {
                    player.Arena.ReleaseInterface(ref freqManager);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = "[{-f}] <ship number>",
            Description = "Sets the targets to the specified ship. The argument must be a\n" +
            "number from 1 (Warbird) to 8 (Shark), or 9 (Spec).\n" +
            "If -f is specified, this command ignores the arena freqman.\n")]
        private void Command_setship(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            bool useFreqManager = true;
            ShipType? ship = null;

            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token;
            while ((token = remaining.GetToken(' ', out remaining)).Length > 0)
            {
                if (token.Equals("-f", StringComparison.Ordinal))
                {
                    useFreqManager = false;
                }
                else if (ship == null && int.TryParse(token, out int shipNumber))
                {
                    ship = (ShipType)Math.Abs(((shipNumber - 1) % (((int)ShipType.Spec) + 1)));
                }
            }

            if (ship == null)
            {
                _chat.SendMessage(player, $"You must specify a ship number from 1 (Warbird) to 8 (Shark), or 9 (Spec).");
                return;
            }

            if (useFreqManager && _capabilityManager.HasCapability(player, Constants.Capabilities.ForceShipFreqChange))
            {
                useFreqManager = false;
            }

            IFreqManager freqManager = useFreqManager ? player.Arena.GetInterface<IFreqManager>() : null;
            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                _playerData.TargetToSet(target, players);

                foreach (Player targetPlayer in players)
                {
                    if (freqManager is not null)
                    {
                        freqManager.ShipChange(targetPlayer, ship.Value, sb);

                        if (sb.Length > 0)
                        {
                            _chat.SendMessage(player, $"{targetPlayer.Name}: {sb}");
                            sb.Clear();
                        }
                    }
                    else
                    {
                        _game.SetShip(targetPlayer, ship.Value);
                    }
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
                _objectPoolManager.PlayerSetPool.Return(players);

                if (freqManager is not null)
                {
                    player.Arena.ReleaseInterface(ref freqManager);
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Team | CommandTarget.Arena,
            Args = null,
            Description = "Sends all of the targets to spectator mode.")]
        private void Command_specall(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.TargetToSet(target, set);

                foreach (Player targetPlayer in set)
                {
                    _game.SetShipAndFreq(targetPlayer, ShipType.Spec, targetPlayer.Arena.SpecFreq);
                }
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
        private void Command_send(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (parameters.IsWhiteSpace())
                return;

            if (!target.TryGetPlayerTarget(out Player targetPlayer))
                return;

            switch (targetPlayer.Type)
            {
                case ClientType.Continuum:
                case ClientType.Chat:
                case ClientType.VIE:
                    _arenaManager.SendToArena(player, parameters, 0, 0);
                    break;

                default:
                    _chat.SendMessage(player, "You can only use ?send on players using Continuum, Subspace or chat clients.");
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
        private void Command_lsmod(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            bool sort = false;
            string substr = null;

            Arena arena = null;

            if (!parameters.IsWhiteSpace())
            {
                ReadOnlySpan<char> remaining = parameters;
                ReadOnlySpan<char> arg;
                while (!(arg = remaining.GetToken(' ', out remaining)).IsEmpty)
                {
                    if (arg.Equals("-a", StringComparison.OrdinalIgnoreCase))
                    {
                        target.TryGetArenaTarget(out arena);
                    }
                    else if (arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
                    {
                        sort = true;
                    }
                    else
                    {
                        substr = arg.ToString();
                    }
                }
            }

            List<string> modulesList = new(); // TODO: use a pool

            _mm.EnumerateModules(
                (moduleType, _) =>
                {
                    string name = moduleType.FullName;
                    if (substr == null || name.Contains(substr))
                        modulesList.Add(name);
                },
                arena);

            if (sort)
            {
                modulesList.Sort(StringComparer.Ordinal);
            }

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                foreach (string str in modulesList)
                {
                    if (sb.Length > 0)
                        sb.Append(", ");

                    sb.Append(str);
                }

                _chat.SendWrappedText(player, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<module name>",
            Description =
            "Displays information about the specified module. This might include a\n" +
            "version number, contact information for the author, and a general\n" +
            "description of the module.")]
        private void Command_modinfo(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            string parametersStr = parameters.ToString();
            if (string.IsNullOrWhiteSpace(parametersStr))
                return;

            var infoArray = _mm.GetModuleInfo(parametersStr);

            if (infoArray.Length > 0)
            {
                foreach (var info in infoArray)
                {
                    _chat.SendMessage(player, info.ModuleTypeName);
                    _chat.SendMessage(player, $"  Type: {info.ModuleQualifiedName}");
                    _chat.SendMessage(player, $"  Assembly Path: {info.AssemblyPath}");
                    _chat.SendMessage(player, $"  Module Type: {(info.IsPlugin ? "plug-in" : "built-in")}");

                    if (info.AttachedArenas.Length > 0)
                    {
                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                        try
                        {
                            foreach (Arena arena in info.AttachedArenas)
                            {
                                if (sb.Length > 0)
                                    sb.Append(", ");

                                sb.Append(arena.Name);
                            }

                            _chat.SendMessage(player, $"  Attached Arenas: {sb}");
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }
                    }

                    _chat.SendMessage(player, $"  Description:");
                    ReadOnlySpan<char> remaining = info.Description;
                    ReadOnlySpan<char> line;
                    while (!(line = remaining.GetToken("\r\n", out remaining)).IsEmpty)
                    {
                        _chat.SendMessage(player, $"    {line}");
                    }
                }
            }
            else
            {
                _chat.SendMessage(player, $"Module '{parameters}' not found.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<module name> [plugin assembly path]",
            Description = "Immediately loads the specified module into the server.")]
        private void Command_insmod(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (parameters.IsWhiteSpace())
                return;

            string moduleTypeName;
            string path;

            int index = parameters.IndexOf(' ');
            if (index == -1)
            {
                moduleTypeName = parameters.ToString();
                path = null;
            }
            else
            {
                moduleTypeName = parameters[..index].ToString();
                path = parameters[(index + 1)..].Trim().Trim("\"\'").ToString();
            }

            if (_mm.LoadModule(moduleTypeName, path))
                _chat.SendMessage(player, $"Module '{moduleTypeName}' loaded.");
            else
                _chat.SendMessage(player, $"Failed to load module '{moduleTypeName}'.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<module name>",
            Description = "Attempts to unload the specified module from the server.")]
        private void Command_rmmod(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (parameters.IsWhiteSpace())
                return;

            if (_mm.UnloadModule(parameters.ToString()))
                _chat.SendMessage(player, $"Module '{parameters}' unloaded.");
            else
                _chat.SendMessage(player, $"Failed to unload module '{parameters}'.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[{-d}] <module name>",
            Description =
            "Attaches the specified module to this arena. Or with {-d},\n" +
            "detaches the module from the arena.")]
        private void Command_attmod(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (parameters.IsEmpty)
                return;

            bool detach = false;
            ReadOnlySpan<char> module = ReadOnlySpan<char>.Empty;

            ReadOnlySpan<char> token;
            while (!(token = parameters.GetToken(' ', out parameters)).IsEmpty)
            {
                if (token.Equals("-d", StringComparison.OrdinalIgnoreCase))
                {
                    detach = true;
                }
                else
                {
                    module = token;
                }
            }

            if (module.IsEmpty)
                return;

            AttachDetachModule(player, module.ToString(), detach);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<module name>",
            Description = "Detaches the module from the arena.")]
        private void Command_detmod(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            AttachDetachModule(player, parameters.ToString(), true);
        }

        private void AttachDetachModule(Player player, string module, bool detach)
        {
            if (player == null)
                return;

            if (detach)
            {
                if (_mm.DetachModule(module, player.Arena))
                    _chat.SendMessage(player, $"Module '{module}' detached.");
                else
                    _chat.SendMessage(player, $"Failed to detach module '{module}'.");
            }
            else
            {
                if (_mm.AttachModule(module, player.Arena))
                    _chat.SendMessage(player, $"Module '{module}' attached.");
                else
                    _chat.SendMessage(player, $"Failed to attach module '{module}'.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.None,
            Args = null,
            Description = "Displays the group of the player, or if none specified, you.")]
        private void Command_getgroup(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (target.TryGetPlayerTarget(out Player targetPlayer))
            {
                _chat.SendMessage(player, $"{targetPlayer.Name} is in group {_groupManager.GetGroup(targetPlayer)}.");
            }
            else if(target.Type == TargetType.Arena)
            {
                _chat.SendMessage(player, $"You are in group {_groupManager.GetGroup(player)}.");
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
        private void Command_setgroup(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
                return;

            bool permanent = false;
            bool global = true;
            ReadOnlySpan<char> groupName = null;

            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token;
            while (!(token = remaining.GetToken(' ', out remaining)).IsEmpty)
            {
                if (token.Equals("-p", StringComparison.OrdinalIgnoreCase))
                    permanent = true;
                else if (token.Equals("-a", StringComparison.OrdinalIgnoreCase))
                    global = false;
                else groupName = token;
            }            

            if (groupName.IsWhiteSpace())
                return;

            if (!_capabilityManager.HasCapability(player, $"higher_than_{groupName}"))
            {
                _chat.SendMessage(player, $"You don't have permission to give people group {groupName}.");
                _logManager.LogP(LogLevel.Warn, nameof(PlayerCommand), player, $"Doesn't have permission to set group '{groupName}'.");
                return;
            }

            // make sure the target isn't in a group already
            string currentGroup = _groupManager.GetGroup(targetPlayer);
            if (!string.Equals(currentGroup, "default"))
            {
                _chat.SendMessage(player, $"Player {targetPlayer.Name} already has a group. You need to use ?rmgroup first.");
                _logManager.LogP(LogLevel.Warn, nameof(PlayerCommand), player, $"Tried to set the group of [{targetPlayer.Name}], who is in '{currentGroup}' already, to '{groupName}'.");
                return;
            }

            if (permanent)
            {
                _groupManager.SetPermGroup(targetPlayer, groupName, global, $"Set by {player.Name} on {DateTime.Now}.");
                _chat.SendMessage(player, $"{targetPlayer.Name} is now in group {groupName}.");
                _chat.SendMessage(targetPlayer, $"You have been assigned to group {groupName} by {player.Name}.");
            }
            else
            {
                _groupManager.SetTempGroup(targetPlayer, groupName);
                _chat.SendMessage(player, $"{targetPlayer.Name} is now temporarily in group {groupName}.");
                _chat.SendMessage(targetPlayer, $"You have been temporarily assigned to group {groupName} by {player.Name}.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = null,
            Description = "Removes the group from a player, returning him to group 'default'. If\n" +
            "the group was assigned for this session only, then it will be removed\n" +
            "for this session; if it is a global group, it will be removed globally;\n" +
            "and if it is an arena group, it will be removed for this arena.")]
        private void Command_rmgroup(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
                return;

            string currentGroup = _groupManager.GetGroup(targetPlayer);
            if (!_capabilityManager.HasCapability(player, $"higher_than_{currentGroup}"))
            {
                _chat.SendMessage(player, $"You don't have permission to take away group {currentGroup}.");
                _logManager.LogP(LogLevel.Warn, nameof(PlayerCommand), player, $"Doesn't have permission to take away group '{currentGroup}'.");
                return;
            }

            _groupManager.RemoveGroup(targetPlayer, $"Set by {player.Name} on {DateTime.Now}");

            _chat.SendMessage(player, $"{targetPlayer.Name} has been removed from group {currentGroup}.");
            _chat.SendMessage(targetPlayer, $"You have been removed from group {currentGroup}.");
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<group name> <password>",
            Description = "Logs you in to the specified group, if the password is correct.")]
        private void Command_grplogin(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            int index = parameters.IndexOf(' ');
            if (index == -1)
            {
                _chat.SendMessage(player, "You must specify a group name and password.");
                return;
            }
            
            ReadOnlySpan<char> groupName = parameters[..index];
            ReadOnlySpan<char> passwordSpan = parameters[(index + 1)..];

            if (_groupManager.CheckGroupPassword(groupName, passwordSpan))
            {
                _groupManager.SetTempGroup(player, groupName);
                _chat.SendMessage(player, $"You are now in group {groupName}.");
            }
            else
            {
                _chat.SendMessage(player, $"Bad password for group {groupName}.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Lists all staff members logged on, which arena they are in, and\n" +
            "which group they belong to.\n")]
        private void Command_listmod(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            bool canSeePrivateArenas = _capabilityManager.HasCapability(player, Constants.Capabilities.SeePrivArena);
            bool canSeeAllStaff = _capabilityManager.HasCapability(player, Constants.Capabilities.SeeAllStaff);

            _playerData.Lock();

            try
            {
                foreach (Player otherPlayer in _playerData.Players)
                {
                    if (otherPlayer.Status != PlayerState.Playing)
                        continue;

                    string group = _groupManager.GetGroup(otherPlayer);

                    if (_capabilityManager.HasCapability(otherPlayer, Constants.Capabilities.IsStaff))
                    {
                        _chat.SendMessage(player, $": {otherPlayer.Name,20} {((!otherPlayer.Arena.IsPrivate || canSeePrivateArenas || player.Arena == otherPlayer.Arena) ? otherPlayer.Arena.Name : "(private)"),10} {group}");
                    }
                    else if (canSeeAllStaff
                        && !string.Equals(group, "default", StringComparison.Ordinal)
                        && !string.Equals(group, "none", StringComparison.Ordinal))
                    {
                        _chat.SendMessage(player, $": {otherPlayer.Name,20} {((!otherPlayer.Arena.IsPrivate || canSeePrivateArenas || player.Arena == otherPlayer.Arena) ? otherPlayer.Arena.Name : "(private)"),10} ({group})");
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<all or part of a player name>",
            Description = "Tells you where the specified player is right now. If you specify\n" +
            "only part of a player name, it will try to find a matching name\n" +
            "using a case insensitive substring search.")]
        private void Command_find(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (target.Type != TargetType.Arena 
                || parameters.IsWhiteSpace())
            {
                return;
            }

            int score = int.MaxValue; // lower is better, -1 means it's an exact match
            StringBuilder bestPlayer = _objectPoolManager.StringBuilderPool.Get();
            StringBuilder bestArena = _objectPoolManager.StringBuilderPool.Get();

            _playerData.Lock();

            try
            {
                foreach (Player otherPlayer in _playerData.Players)
                {
                    if (otherPlayer.Status != PlayerState.Playing)
                        continue;

                    if (parameters.Equals(otherPlayer.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        // exact match
                        bestPlayer.Clear();
                        bestPlayer.Append(otherPlayer.Name);
                        bestArena.Clear();
                        bestArena.Append(otherPlayer.Arena?.Name);
                        score = -1;
                        break;
                    }

                    int index = otherPlayer.Name.AsSpan().IndexOf(parameters, StringComparison.OrdinalIgnoreCase);
                    if (index != -1)
                    {
                        // for substring matches, the score is the distance from the start of the name
                        if (index < score)
                        {
                            bestPlayer.Clear();
                            bestPlayer.Append(otherPlayer.Name);
                            bestArena.Clear();
                            bestArena.Append(otherPlayer.Arena?.Name);
                            score = index;
                        }
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            if (score > 0) // there's a chance there could be a better match in a peer zone
            {
                IPeer peer = _broker.GetInterface<IPeer>();
                if (peer is not null)
                {
                    try
                    {
                        peer.FindPlayer(parameters, ref score, bestPlayer, bestArena);
                    }
                    finally
                    {
                        _broker.ReleaseInterface(ref peer);
                    }
                }
            }

            if (bestPlayer.Length > 0
                && bestArena.Length > 0)
            {
                if (bestArena[0] != '#'
                    || _capabilityManager.HasCapability(player, Constants.Capabilities.SeePrivArena)
                    || IsInArena(player, bestArena))
                {
                    _chat.SendMessage(player, $"{bestPlayer} is in arena {bestArena}.");
                }
                else
                {
                    _chat.SendMessage(player, $"{bestPlayer} is in a private arena.");
                }
            }

            if (score != -1)
            {
                // exact match not found
                // have the command manager send it as the default command (to be handled by the billing server)
                _commandManager.Command($"\\find {parameters}", player, target, ChatSound.None);
            }

            static bool IsInArena(Player player, StringBuilder arenaName)
            {
                if (player.Arena is null)
                    return false;

                Span<char> arenaSpan = stackalloc char[arenaName.Length];
                arenaName.CopyTo(0, arenaSpan, arenaName.Length);
                return MemoryExtensions.Equals(player.Arena.Name, arenaSpan, StringComparison.OrdinalIgnoreCase);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player,
            Args = null,
            Description = "Displays the current location (on the map) of the target player.")]
        private void Command_where(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player targetPlayer))
                targetPlayer = player;

            // right shift by 4 is divide by 16 (each tile is 16 pixels)
            int x = targetPlayer.Position.X >> 4;
            int y = targetPlayer.Position.Y >> 4;

            string name = (targetPlayer == player) ? "You" : targetPlayer.Name;
            string verb = (targetPlayer == player) ? "are" : "is";

            if (targetPlayer.IsStandard)
            {
                _chat.SendMessage(player, $"{name} {verb} at {(char)('A' + (x * 20 / 1024))}{(y * 20 / 1024 + 1)} ({x},{y})");
            }
            else
            {
                _chat.SendMessage(player, $"{name} {verb} not using a playable client.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Arena,
            Args = "see description", 
            Description = "Modifies the chat mask for the target player, or if no target, for the\n" +
            "current arena. The arguments must all be of the form\n" +
            "{(-|+)(pub|pubmacro|freq|nmefreq|priv|chat|modchat|all)} or {-t <seconds>}.\n" +
            "A minus sign and then a word disables that type of chat, and a plus sign\n" +
            "enables it. The special type {all} means to apply the plus or minus to\n" +
            "all of the above types. {-t} lets you specify a timeout in seconds.\n" +
            "The mask will be effective for that time, even across logouts.\n" +
            "\n" +
            "Examples:\n" +
            " * If someone is spamming public macros: {:player:?setcm -pubmacro -t 600}\n" +
            " * To disable all blue messages for this arena: {?setcm -pub -pubmacro}\n" +
            " * An equivalent to *shutup: {:player:?setcm -all}\n" +
            " * To restore chat to normal: {?setcm +all}\n" +
            "\n" +
            "Current limitations: You can't currently restrict a particular\n" +
            "frequency. Leaving and entering an arena will remove a player's chat\n" +
            "mask, unless it has a timeout.\n")]
        private void Command_setcm(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ChatMask mask;
            Player targetPlayer = null;

            // get the current mask
            if (target.TryGetArenaTarget(out Arena arena))
            {
                mask = _chat.GetArenaChatMask(arena);
            }
            else if (target.TryGetPlayerTarget(out targetPlayer))
            {
                mask = _chat.GetPlayerChatMask(targetPlayer);
            }
            else
            {
                _chat.SendMessage(player, "Bad target.");
                return;
            }

            int timeout = 0;

            // read the parameters, updating what's needed in the mask
            ReadOnlySpan<char> remaining = parameters;
            ReadOnlySpan<char> token;
            while ((token = remaining.GetToken(' ', out remaining)).Length > 0)
            {
                bool all = false;

                if (token[0] == '-' || token[0] == '+')
                {
                    bool isRestricted = token[0] == '-';
                    ReadOnlySpan<char> chatType = token[1..];
                    
                    if (MemoryExtensions.Equals(chatType, "all", StringComparison.OrdinalIgnoreCase))
                        all = true;

                    if (all || MemoryExtensions.Equals(chatType, "pubmacro", StringComparison.OrdinalIgnoreCase))
                        mask.Set(ChatMessageType.PubMacro, isRestricted);

                    if (all || MemoryExtensions.Equals(chatType, "pub", StringComparison.OrdinalIgnoreCase))
                        mask.Set(ChatMessageType.Pub, isRestricted);

                    if (all || MemoryExtensions.Equals(chatType, "freq", StringComparison.OrdinalIgnoreCase))
                        mask.Set(ChatMessageType.Freq, isRestricted);

                    if (all || MemoryExtensions.Equals(chatType, "nmefreq", StringComparison.OrdinalIgnoreCase))
                        mask.Set(ChatMessageType.EnemyFreq, isRestricted);

                    if (all || MemoryExtensions.Equals(chatType, "priv", StringComparison.OrdinalIgnoreCase))
                        mask.Set(ChatMessageType.Private, isRestricted);

                    if (all || MemoryExtensions.Equals(chatType, "chat", StringComparison.OrdinalIgnoreCase))
                        mask.Set(ChatMessageType.Chat, isRestricted);

                    if (all || MemoryExtensions.Equals(chatType, "modchat", StringComparison.OrdinalIgnoreCase))
                        mask.Set(ChatMessageType.ModChat, isRestricted);

                    if (MemoryExtensions.Equals(token, "-time", StringComparison.OrdinalIgnoreCase)
                        || MemoryExtensions.Equals(token, "-t", StringComparison.OrdinalIgnoreCase))
                    {
                        token = remaining.GetToken(' ', out remaining);
                        if (token.Length > 0)
                        {
                            _ = int.TryParse(token, out timeout);
                        }
                    }
                }
            }

            // install the updated mask
            if (arena != null)
            {
                _chat.SetArenaChatMask(arena, mask);
            }
            else if (targetPlayer != null)
            {
                _chat.SetPlayerChatMask(targetPlayer, mask, timeout);
            }

            // output the mask
            Command_getcm("getcm", "", player, target);
        }

        [CommandHelp(
            Targets = CommandTarget.Player | CommandTarget.Arena,
            Args = null,
            Description = "Prints out the chat mask for the target player, or if no target, for the\n" +
            "current arena. The chat mask specifies which types of chat messages are\n" +
            "allowed.")]
        private void Command_getcm(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ChatMask mask;

            if (target.TryGetArenaTarget(out Arena arena))
            {
                mask = _chat.GetArenaChatMask(arena);

                _chat.SendMessage(player, $"Arena {arena.Name}:" +
                    $" {(mask.IsRestricted(ChatMessageType.Pub) ? '-' : '+')}pub" +
                    $" {(mask.IsRestricted(ChatMessageType.PubMacro) ? '-' : '+')}pubmacro" +
                    $" {(mask.IsRestricted(ChatMessageType.Freq) ? '-' : '+')}freq" +
                    $" {(mask.IsRestricted(ChatMessageType.EnemyFreq) ? '-' : '+')}nmefreq" +
                    $" {(mask.IsRestricted(ChatMessageType.Private) ? '-' : '+')}priv" +
                    $" {(mask.IsRestricted(ChatMessageType.Chat) ? '-' : '+')}chat" +
                    $" {(mask.IsRestricted(ChatMessageType.ModChat) ? '-' : '+')}modchat");
            }
            else if (target.TryGetPlayerTarget(out Player targetPlayer))
            {
                _chat.GetPlayerChatMask(targetPlayer, out mask, out TimeSpan? remaining);

                _chat.SendMessage(player, $"{targetPlayer.Name}:" +
                    $" {(mask.IsRestricted(ChatMessageType.Pub) ? '-' : '+')}pub" +
                    $" {(mask.IsRestricted(ChatMessageType.PubMacro) ? '-' : '+')}pubmacro" +
                    $" {(mask.IsRestricted(ChatMessageType.Freq) ? '-' : '+')}freq" +
                    $" {(mask.IsRestricted(ChatMessageType.EnemyFreq) ? '-' : '+')}nmefreq" +
                    $" {(mask.IsRestricted(ChatMessageType.Private) ? '-' : '+')}priv" +
                    $" {(mask.IsRestricted(ChatMessageType.Chat) ? '-' : '+')}chat" +
                    $" {(mask.IsRestricted(ChatMessageType.ModChat) ? '-' : '+')}modchat" +
                    $" -t {(remaining == null ? "no expiration (valid until arena change)" : $"{remaining.Value}")}");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<arena name>", 
            Description = "Lists the players in the given arena.")]
        private void Command_listarena(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            ReadOnlySpan<char> arenaName = !parameters.IsWhiteSpace() ? parameters : player.Arena?.Name;
            if (arenaName.IsWhiteSpace())
                return;

            Arena arena = _arenaManager.FindArena(arenaName);
            if (arena == null)
            {
                _chat.SendMessage(player, $"Arena '{arenaName}' doesn't exist.");
                return;
            }

            int total = 0;
            int playing = 0;
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                _playerData.Lock();

                try
                {
                    foreach (Player otherPlayer in _playerData.Players)
                    {
                        if (otherPlayer.Status == PlayerState.Playing
                            && otherPlayer.Arena == arena)
                        {
                            total++;

                            if (otherPlayer.Ship != ShipType.Spec)
                                playing++;

                            if (sb.Length > 0)
                                sb.Append(", ");

                            sb.Append(otherPlayer.Name);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                _chat.SendMessage(player, $"Arena '{arena.Name}': {total} total, {playing} playing");
                _chat.SendWrappedText(player, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        [CommandHelp(
            Targets = CommandTarget.Arena,
            Args = null,
            Description = "Displays information about the map in this arena.")]
        private void Command_mapinfo(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena arena = player.Arena;
            if (arena == null)
                return;

            string fileName = _mapData.GetMapFilename(arena, null);

            _chat.SendMessage(player, $"LVL file loaded from '{(!string.IsNullOrWhiteSpace(fileName) ? fileName : "<nowhere>")}'.");

            const string NotSet = "<not set>";

            _chat.SendMessage(player,
                $"name: {_mapData.GetAttribute(arena, MapAttributeKeys.Name) ?? NotSet}, " +
                $"version: {_mapData.GetAttribute(arena, MapAttributeKeys.Version) ?? NotSet}");

            _chat.SendMessage(player,
                $"map creator: {_mapData.GetAttribute(arena, MapAttributeKeys.MapCreator) ?? NotSet}, " +
                $"tileset creator: {_mapData.GetAttribute(arena, MapAttributeKeys.TilesetCreator) ?? NotSet}, " +
                $"program: {_mapData.GetAttribute(arena, MapAttributeKeys.Program) ?? NotSet}");

            var errors = _mapData.GetErrors(arena);

            _chat.SendMessage(player,
                $"tiles:{_mapData.GetTileCount(arena)} " +
                $"flags:{_mapData.GetFlagCount(arena)} " +
                $"regions:{_mapData.GetRegionCount(arena)} " +
                $"errors:{errors.Count}");

            if (errors.Count > 0 && _capabilityManager.HasCapability(player, Constants.Capabilities.IsStaff))
            {
                _chat.SendMessage(player, "Error details:");
                foreach (var error in errors)
                {
                    _chat.SendMessage(player, $"- {error}");
                }
            }

            // TODO: estimated memory stats
        }

        /// <summary>
        /// The default image format for downloading an image of the map.
        /// </summary>
        private const string DefaultMapImageFormat = "png";

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[<image file extension>]",
            Description = "Downloads an image of the map.\n" +
            $"The image format can optionally be specified. The default is '{DefaultMapImageFormat}'.")]
        private void Command_mapimage(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!player.IsStandard)
            {
                _chat.SendMessage(player, "Your client does not support file transfers.");
                return;
            }

            Arena arena = player.Arena;
            if (arena == null)
                return;

            string mapPath = _mapData.GetMapFilename(arena, null);
            if (string.IsNullOrWhiteSpace(mapPath))
                return;

            parameters = parameters.Trim().Trim('.');
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                if (parameters.Contains(c))
                {
                    _chat.SendMessage(player, "Invalid image file extension.");
                    return;
                }
            }

            ReadOnlySpan<char> extension = !parameters.IsWhiteSpace() ? parameters : DefaultMapImageFormat;

            const string prefix = "mapimage-";
            Span<char> fileName = stackalloc char[prefix.Length + 32 + 1 + extension.Length];
            bool success = fileName.TryWrite($"{prefix}{Guid.NewGuid():N}.{extension}", out int charsWritten);
            Debug.Assert(success && fileName.Length == charsWritten);

            string path = Path.Join("tmp", fileName);

            try
            {
                _mapData.SaveImage(arena, path);
            }
            catch (Exception ex)
            {
                _chat.SendMessage(player, $"Error saving image.");
                _chat.SendWrappedText(player, ex.Message);
                return;
            }

            string imageFileName = $"{Path.GetFileNameWithoutExtension(mapPath.AsSpan())}.{extension}";
            _fileTransfer.SendFile(player, path, imageFileName, true);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Causes the flag game to immediately reset.")]
        private void Command_flagreset(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena arena = player.Arena;
            if (arena == null)
                return;

            IFlagGame flagGame = arena.GetInterface<IFlagGame>();
            if (flagGame == null)
            {
                _chat.SendMessage(player, $"No flag game to reset.");
                return;
            }

            flagGame.ResetGame(player.Arena);
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[<new # of balls> | +<balls to add> | -<balls to remove>]",
            Description =
            "Displays or changes the number of balls in the arena.\n" +
            "A number without a plus or minus sign is taken as a new count.\n" +
            "A plus signifies adding that many, and a minus removes that many.\n" +
            "Continuum currently supports only eight balls.")]
        private void Command_ballcount(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena arena = player.Arena;
            if (arena == null)
                return;

            if (parameters.IsWhiteSpace())
            {
                if (_balls.TryGetBallSettings(arena, out BallSettings ballSettings))
                {
                    _chat.SendMessage(player, $"Ball count: {ballSettings.BallCount}");
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
        private void Command_giveball(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena arena = player.Arena;
            if (arena == null)
                return;

            bool force = false;
            byte ballId = 0;

            ReadOnlySpan<char> remaining = parameters;

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
                    _chat.SendMessage(player, "Invalid ball ID.");
                    return;
                }
            }
            while (!remaining.IsEmpty);

            if (!_balls.TryGetBallSettings(arena, out BallSettings ballSettings)
                || ballId >= ballSettings.BallCount)
            {
                _chat.SendMessage(player, $"Ball {ballId} doesn't exist. Use ?ballcount to add balls to the arena.");
                return;
            }

            if (!target.TryGetPlayerTarget(out Player targetPlayer))
                targetPlayer = player;

            if (targetPlayer.Ship == ShipType.Spec)
            {
                if (targetPlayer == player)
                    _chat.SendMessage(player, "You are in spec.");
                else
                    _chat.SendMessage(player, $"{targetPlayer.Name} is in spec.");
            }
            else if (targetPlayer.Arena != player.Arena || targetPlayer.Status != PlayerState.Playing)
            {
                _chat.SendMessage(player, $"{targetPlayer.Name} is not in this arena.");
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
                    if (targetPlayer != player)
                    {
                        _chat.SendMessage(player, $"Gave ball {ballId} to {targetPlayer.Name}.");
                    }
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "<ball id> <x-coord> <y-coord>",
            Description = "Move the specified ball to the specified coordinates.")]
        private void Command_moveball(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena arena = player.Arena;
            if (arena == null)
                return;

            ReadOnlySpan<char> token = parameters.GetToken(' ', out ReadOnlySpan<char> remaining);
            if (token.IsEmpty || !byte.TryParse(token, out byte ballId))
            {
                _chat.SendMessage(player, "Invalid ball ID.");
                return;
            }

            if (!_balls.TryGetBallSettings(arena, out BallSettings ballSettings)
                || ballId >= ballSettings.BallCount)
            {
                _chat.SendMessage(player, $"Ball {ballId} doesn't exist. Use ?ballcount to add balls to the arena.");
                return;
            }

            token = remaining.GetToken(' ', out remaining);
            if (token.IsEmpty || !short.TryParse(token, out short x) || x < 0 || x >= 1024)
            {
                _chat.SendMessage(player, "Invalid x-coordinate.");
                return;
            }

            if (!short.TryParse(remaining, out short y) || y < 0 || y >= 1024)
            {
                _chat.SendMessage(player, "Invalid y-coordinate.");
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
                _chat.SendMessage(player, $"Moved ball {ballId} to ({x},{y}).");
            }
            else
            {
                _chat.SendMessage(player, $"Failed to moved ball {ballId} to ({x},{y}).");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = "[<ball id>]",
            Description =
            "Resets the specified existing ball back to its spawn location.\n" +
            "If no ball is specified, ball id 0 is assumed.")]
        private void Command_spawnball(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena arena = player.Arena;
            if (arena == null)
                return;

            int ballId;

            if (parameters.IsWhiteSpace())
            {
                ballId = 0;
            }
            else if (!int.TryParse(parameters, out ballId))
            {
                _chat.SendMessage(player, "Invalid ball ID.");
                return;
            }

            if (_balls.TrySpawnBall(arena, ballId))
            {
                _chat.SendMessage(player, $"Respawned ball {ballId}.");
            }
            else
            {
                _chat.SendMessage(player, $"Failed to respawn ball {ballId}. Check that the ball exists. Use ?ballcount to add more balls to the arena.");
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None,
            Args = null,
            Description = "Displays the last known position of balls, as well as the player who is carrying it or who fired it, if applicable.")]
        private void Command_ballinfo(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            Arena arena = player.Arena;
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
                                _chat.SendMessage(player, $"Ball {ballId}: shot by {ballData.Carrier.Name} (freq {ballData.Freq}) " +
                                    $"from {(char)('A' + x)}{y + 1} ({ballData.X / 16},{ballData.Y / 16})");
                            }
                            else
                            {
                                _chat.SendMessage(player, $"Ball {ballId}: on map (freq {ballData.Freq}) " +
                                    $"{((ballData.XSpeed != 0 || ballData.YSpeed != 0) ? "last seen" : "still")} " +
                                    $"at {(char)('A' + x)}{y + 1} ({ballData.X / 16},{ballData.Y / 16})");
                            }
                            break;

                        case BallState.Carried:
                            _chat.SendMessage(player, $"Ball {ballId}: carried by {ballData.Carrier.Name} (freq {ballData.Freq}) " +
                                $"at {(char)('A' + x)}{y + 1} ({ballData.Carrier.Position.X / 16},{ballData.Carrier.Position.Y / 16})");
                            break;

                        case BallState.Waiting:
                            _chat.SendMessage(player, $"Ball {ballId}: waiting to be respawned.");
                            break;
                    }
                }
            }
        }

        [CommandHelp(
            Targets = CommandTarget.None, 
            Description = "Displays information about server internals. It currently includes information about object pooling.")]
        private void Command_serverstats(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            IObjectPoolManager poolManager = _broker.GetInterface<IObjectPoolManager>();
            if (poolManager != null)
            {
                try
                {
                    _chat.SendMessage(player, "Object Pooling Statistics (available/total):");

                    foreach (var pool in poolManager.Pools)
                    {
                        _chat.SendMessage(player, $"{pool.Type} ({pool.ObjectsAvailable}/{pool.ObjectsCreated})");
                    }
                }
                finally
                {
                    _broker.ReleaseInterface(ref poolManager);
                }
            }
        }

        #region Command group helper classes

        private class InterfaceFieldInfo
        {
            public readonly FieldInfo FieldInfo;
            public int ReferenceCount = 0;

            public InterfaceFieldInfo(FieldInfo fieldInfo)
            {
                FieldInfo = fieldInfo ?? throw new ArgumentNullException(nameof(fieldInfo));
            }
        }

        private class CommandGroup
        {
            public string Name;
            public HashSet<Type> InterfaceDependencies { get; init; }
            public CommandInfo[] Commands { get; init; }
            public bool IsLoaded;

            public CommandGroup(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("Cannot be null or white-space.", nameof(name));

                Name = name;
            }
        }

        private class CommandInfo
        {
            public readonly string CommandName;
            public readonly CommandDelegate CommandDelegate;
            public readonly CommandWithSoundDelegate CommandWithSoundDelegate;

            public CommandInfo(string commandName, CommandDelegate commandDelegate) : this(commandName)
            {
                CommandDelegate = commandDelegate ?? throw new ArgumentNullException(nameof(commandDelegate));
            }

            public CommandInfo(string commandName, CommandWithSoundDelegate commandSoundDelegate) : this(commandName)
            {
                CommandWithSoundDelegate = commandSoundDelegate ?? throw new ArgumentNullException(nameof(commandSoundDelegate));
            }

            private CommandInfo(string commandName)
            {
                if (string.IsNullOrWhiteSpace(commandName))
                    throw new ArgumentException("Cannot be null or white-space.", nameof(commandName));

                CommandName = commandName;
            }
        }

        #endregion

        private class ArenaListItem
        {
            private readonly char[] _arenaNameChars = new char[Constants.MaxArenaNameLength];
            private int _length = 0;
            public ReadOnlySpan<char> ArenaName => new(_arenaNameChars, 0, _length);

            public int PlayerCount { get; private set; } = 0;
            public bool IsCurrent { get; private set; } = false;

            public void Set(ReadOnlySpan<char> arenaName, int playerCount, bool isCurrent)
            {
                if (arenaName.Length > Constants.MaxArenaNameLength)
                    throw new ArgumentException($"Length exceeds the maximum allowed for an arena name ({Constants.MaxArenaNameLength}).", nameof(arenaName));

                arenaName.CopyTo(_arenaNameChars);
                _length = arenaName.Length;
                PlayerCount = playerCount;
                IsCurrent = isCurrent;
            }

            public void Clear()
            {
                Set("", 0, false);
            }
        }

        private class ArenaListItemComparer : IComparer<ArenaListItem>
        {
            public static readonly ArenaListItemComparer Instance = new();

            public int Compare(ArenaListItem x, ArenaListItem y)
            {
                // first, partitioned with empty arenas at the end
                if (x.PlayerCount == 0)
                {
                    if (y.PlayerCount != 0)
                        return 1;
                }
                else
                {
                    if (y.PlayerCount == 0)
                        return -1;
                }

                // within those partitions, partition with private arenas at the end
                if (x.ArenaName[0] == '#')
                {
                    if (y.ArenaName[0] != '#')
                        return 1;
                }
                else
                {
                    if (y.ArenaName[0] == '#')
                        return -1;
                }

                // order by player count desc
                int value = x.PlayerCount - y.PlayerCount;
                if (value != 0)
                    return value;

                // order by arena name asc
                return MemoryExtensions.CompareTo(x.ArenaName, y.ArenaName, StringComparison.OrdinalIgnoreCase);
            }
        }

        private class ArenaListItemPooledObjectPolicy : IPooledObjectPolicy<ArenaListItem>
        {
            public ArenaListItem Create()
            {
                return new ArenaListItem();
            }

            public bool Return(ArenaListItem obj)
            {
                if (obj is null)
                    return false;

                obj.Clear();
                return true;
            }
        }

        private class ArenaListItemListPooledObjectPolicy : IPooledObjectPolicy<List<ArenaListItem>>
        {
            public List<ArenaListItem> Create()
            {
                return new List<ArenaListItem>();
            }

            public bool Return(List<ArenaListItem> obj)
            {
                if (obj is null)
                    return false;

                obj.Clear();
                return true;
            }
        }
    }
}

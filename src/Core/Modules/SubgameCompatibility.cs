using SS.Core.ComponentAdvisors;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core.Modules
{
	/// <summary>
	/// Module that provides compatibility to use bots that were written to use subgame commands.
	/// </summary>
	[CoreModuleInfo]
    public sealed class SubgameCompatibility : IModule, IChatAdvisor
    {
        // Required dependencies
        private readonly IChat _chat;
        private readonly ICommandManager _commandManager;
        private readonly IConfigManager _configManager;
        private readonly IGame _game;
        private readonly IGroupManager _groupManager;
        private readonly ILagQuery _lagQuery;
        private readonly ILogManager _logManager;
        private readonly INetwork _network;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;
        private readonly IScoreStats _scoreStats;

        // Optional dependencies
        private IBilling? _billing;
        private IIdle? _idle;

        private AdvisorRegistrationToken<IChatAdvisor>? _chatAdvisorToken;

        private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            {"?recycle",    "recyclearena"},
            {"?get",        "geta"},
            //{"?set",        "seta"}, // ?set is based on the SGCompat:TemporarySet setting (see the ReadConfig method)
            {"?setlevel",   "putmap"},
            {"*listban",    "listmidbans"},
            {"*removeban",  "delmidban"},
            {"*kill",       "kick -m"},
            {"*log",        "lastlog"},
            {"*zone",       "az"},
            {"*flags",      "flaginfo"},
            {"*locate",     "find"},
            {"*recycle",    "shutdown -r"},
            {"*sysop",      "setgroup sysop"},
            {"*smoderator", "setgroup smod"},
            {"*moderator",  "setgroup mod"},
            {"*arena",      "aa"},
            {"*einfo",      "sg_einfo"},
            {"*tinfo",      "sg_tinfo"},
            {"*listmod",    "sg_listmod"},
            {"*where",      "sg_where"},
            {"*info",       "sg_info"},
            {"*lag",        "sg_lag"},
            {"*spec",       "sg_spec"},
            {"*lock",       "sg_lock"},
            {"*setship",    "setship -f"},
            {"*setfreq",    "setfreq -f"},
            {"*scorereset", "sg_scorereset"},
        };

        private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _aliasesLookup;

        public SubgameCompatibility(
            IChat chat,
            ICommandManager commandManager,
            IConfigManager configManager,
            IGame game,
            IGroupManager groupManager,
            ILagQuery lagQuery,
            ILogManager logManager,
            INetwork network,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IScoreStats scoreStats)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _game = game ?? throw new ArgumentNullException(nameof(game));
            _groupManager = groupManager ?? throw new ArgumentNullException(nameof(groupManager));
            _lagQuery = lagQuery ?? throw new ArgumentNullException(nameof(lagQuery));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _scoreStats = scoreStats ?? throw new ArgumentNullException(nameof(scoreStats));

            _aliasesLookup = _aliases.GetAlternateLookup<ReadOnlySpan<char>>();
		}

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _billing = broker.GetInterface<IBilling>();
            _idle = broker.GetInterface<IIdle>();

            ReadConfig();

            _commandManager.AddCommand("sg_einfo", Command_sg_einfo);
            _commandManager.AddCommand("sg_tinfo", Command_sg_tinfo);
            _commandManager.AddCommand("sg_listmod", Command_sg_listmod);
            _commandManager.AddCommand("sg_where", Command_sg_where);
            _commandManager.AddCommand("sg_info", Command_sg_info);
            _commandManager.AddCommand("sg_lag", Command_sg_lag);
            _commandManager.AddCommand("sg_spec", Command_sg_spec);
            _commandManager.AddCommand("sg_lock", Command_sg_lock);
            _commandManager.AddCommand("sg_scorereset", Command_sg_scorereset);

            GlobalConfigChangedCallback.Register(broker, ReadConfig);

            _chatAdvisorToken = broker.RegisterAdvisor<IChatAdvisor>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            broker.UnregisterAdvisor(ref _chatAdvisorToken);

            GlobalConfigChangedCallback.Unregister(broker, ReadConfig);

            _commandManager.RemoveCommand("sg_einfo", Command_sg_einfo);
            _commandManager.RemoveCommand("sg_tinfo", Command_sg_tinfo);
            _commandManager.RemoveCommand("sg_listmod", Command_sg_listmod);
            _commandManager.RemoveCommand("sg_where", Command_sg_where);
            _commandManager.RemoveCommand("sg_info", Command_sg_info);
            _commandManager.RemoveCommand("sg_lag", Command_sg_lag);
            _commandManager.RemoveCommand("sg_spec", Command_sg_spec);
            _commandManager.RemoveCommand("sg_lock", Command_sg_lock);
            _commandManager.RemoveCommand("sg_scorereset", Command_sg_scorereset);

            if (_billing is not null)
            {
                broker.ReleaseInterface(ref _billing);
            }

            if (_idle is not null)
            {
                broker.ReleaseInterface(ref _idle);
            }

            return true;
        }

        #endregion

        #region IChatAdvisor

        bool IChatAdvisor.TryRewriteCommand(char commandChar, ReadOnlySpan<char> line, Span<char> buffer, out int charsWritten)
        {
            if (commandChar != '?' && commandChar != '*')
            {
                charsWritten = 0;
                return false;
            }

            ReadOnlySpan<char> originalCommand = line.GetToken(" =", out ReadOnlySpan<char> remaining);
            if (originalCommand.IsEmpty)
            {
                charsWritten = 0;
                return false;
            }

            Span<char> key = stackalloc char[originalCommand.Length + 1];
            if (!key.TryWrite($"{commandChar}{originalCommand}", out _))
            {
                charsWritten = 0;
                return false;
            }

            if (!_aliasesLookup.TryGetValue(key, out string? replacementCommand))
            {
                charsWritten = 0;
                return false;
            }

            bool success = buffer.TryWrite($"{replacementCommand}{remaining}", out charsWritten);
            if (success)
            {
                _logManager.LogM(LogLevel.Drivel, nameof(SubgameCompatibility), $"Command rewritten from {key} to {replacementCommand}");
            }
            else
            {
                _logManager.LogM(LogLevel.Warn, nameof(SubgameCompatibility), $"Failed to rewrite command from {key} to {replacementCommand}");
            }

            return success;
        }

        #endregion

        #region Commands

        private void Command_sg_einfo(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                targetPlayer = player;
            }

            string proxy = _network.GetProxyUsage(targetPlayer) switch
            {
                ProxyUsage.NoProxy => "Not using proxy",
                ProxyUsage.SOCKS5 => "SOCKS5 proxy",
                ProxyUsage.LocalProxy => "Using proxy at localhost",
                ProxyUsage.CustomProxy => "Using custom proxy",
                ProxyUsage.NAT => "Using NAT",
                ProxyUsage.NotConfigured => "Listen:BindAddress is not set in global.conf",
                ProxyUsage.Undetermined or _ => "Undetermined",
            };

            int? drift = null;

            if (targetPlayer.IsStandard)
            {
                drift = _lagQuery.QueryTimeSyncDrift(targetPlayer);
            }

            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                sb.Append($"{targetPlayer.Name}: UserId: ");

                if (_billing is not null && _billing.TryGetUserId(targetPlayer, out uint userId))
                {
                    sb.Append(userId);
                }
                else
                {
                    sb.Append("-1");
                }

                sb.Append($"  Res: {targetPlayer.Xres}x{targetPlayer.Yres}  Client: {targetPlayer.ClientName}  Proxy: {proxy}  Idle: {(_idle is not null ? (long)_idle.GetIdle(targetPlayer).TotalSeconds : -1)} s  Timer drift: {(drift is not null ? drift.Value : 0)}");

                _chat.SendMessage(player, sb);
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        private void Command_sg_tinfo(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                targetPlayer = player;
            }

            if (!targetPlayer.IsStandard)
                return;

            List<TimeSyncRecord> records = []; // TODO: pool
            _lagQuery.QueryTimeSyncHistory(targetPlayer, records);

            if (records.Count == 0)
                return;

            _chat.SendMessage(player, $"{"ServerTime",11} {"UserTime",11} {"Diff",11}");

            foreach (TimeSyncRecord record in records)
            {
                _chat.SendMessage(player, $"{record.ServerTime,11} {record.ClientTime,11} {record.ServerTime - record.ClientTime,11}");
            }
        }

        private void Command_sg_listmod(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            _playerData.Lock();

            try
            {
                foreach (Player otherPlayer in _playerData.Players)
                {
                    if (otherPlayer.Status != PlayerState.Playing)
                        continue;

                    string group = _groupManager.GetGroup(otherPlayer);
                    if (!string.Equals(group, "default", StringComparison.OrdinalIgnoreCase))
                    {
                        _chat.SendMessage(player, $"{otherPlayer.Name} - {group} - {otherPlayer.Arena?.Name ?? "(no arena)"}");
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }
        }

        private void Command_sg_where(ReadOnlySpan<char> command, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
                targetPlayer = player;

            // right shift by 4 is divide by 16 (each tile is 16 pixels)
            int x = targetPlayer.Position.X >> 4;
            int y = targetPlayer.Position.Y >> 4;

            if (targetPlayer.IsStandard)
            {
                _chat.SendMessage(player, $"{targetPlayer.Name}: {(char)('A' + (x * 20 / 1024))}{(y * 20 / 1024 + 1)}");
            }
            else
            {
                _chat.SendMessage(player, $"{targetPlayer.Name}: A1");
            }
        }

        private void Command_sg_info(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                targetPlayer = player;
            }

            NetConnectionStats stats = new();
            _network.GetConnectionStats(targetPlayer, ref stats);

            _lagQuery.QueryPositionPing(targetPlayer, out PingSummary positionPing);
            _lagQuery.QueryClientPing(targetPlayer, out ClientPingSummary clientPing);
            _lagQuery.QueryReliablePing(targetPlayer, out PingSummary reliablePing);
            _lagQuery.QueryPacketloss(targetPlayer, out PacketlossSummary packetlossSummary, out PacketlossDetails packetlossDetails);
            _lagQuery.QueryReliableLag(targetPlayer, out ReliableLagData reliableLag);

            int current = (positionPing.Current + clientPing.Current + 2 * reliablePing.Current) / 4;
            int average = (positionPing.Average + clientPing.Average + 2 * reliablePing.Average) / 4;
            int low = Math.Min(Math.Min(positionPing.Min, clientPing.Min), reliablePing.Min);
            int high = Math.Max(Math.Max(positionPing.Max, clientPing.Max), reliablePing.Max);

            _chat.SendMessage(player, $"IP:{targetPlayer.IPAddress}  TimeZoneBias:{targetPlayer.TimeZoneBias}  Freq:{targetPlayer.Freq}  TypedName:{targetPlayer.Name}  Demo:0  MachineId:{targetPlayer.MacId}");

            _chat.SendMessage(player, $"Ping:{current}ms  LowPing:{low}ms  HighPing:{high}ms  AvePing:{average}ms");

            ulong relPacketsLost = reliableLag.Retries >= reliableLag.AckDups ? (reliableLag.Retries - reliableLag.AckDups) : 0;
            _chat.SendMessage(player, $"LOSS: S2C:{packetlossSummary.S2C * 100d,4:F1}%  C2S:{packetlossSummary.C2S * 100d,4:F1}%  S2CWeapons:{packetlossSummary.S2CWeapon * 100d,4:F1}%  S2C_RelOut:{relPacketsLost}({reliableLag.ReliablePacketsSent})");
            _chat.SendMessage(player, $"S2C:{packetlossDetails.ServerPacketsSent}-->{packetlossDetails.ClientPacketsReceived}  C2S:{packetlossDetails.ClientPacketsSent}-->{packetlossDetails.ServerPacketsReceived}");
            _chat.SendMessage(player, $"C2S CURRENT: Slow:0 Fast:0 0.0%   TOTAL: Slow:0 Fast:0 0.0%");
            _chat.SendMessage(player, $"S2C CURRENT: Slow:{clientPing.S2CSlowCurrent} Fast:{clientPing.S2CFastCurrent} 0.0%   TOTAL: Slow:{clientPing.S2CSlowTotal} Fast:{clientPing.S2CFastTotal} 0.0%");

            TimeSpan sessionDuration = DateTime.UtcNow - targetPlayer.ConnectTime;
            if (_billing is null || !_billing.TryGetUsage(targetPlayer, out TimeSpan usage, out DateTime? firstLoginTimestamp))
            {
                usage = sessionDuration;
                firstLoginTimestamp = DateTime.MinValue;
            }
            else
            {
                usage += sessionDuration;
            }

            firstLoginTimestamp ??= DateTime.MinValue;

            _chat.SendMessage(player, $"TIME: Session:{(int)sessionDuration.TotalHours,5:D}:{sessionDuration:mm\\:ss}  Total:{(int)usage.TotalHours,5:D}:{usage:mm\\:ss}  Created: {firstLoginTimestamp:yyyy-MM-dd HH:mm:ss}");
            _chat.SendMessage(player, $"Bytes/Sec:{stats.BytesSent / sessionDuration.TotalSeconds:F0}  LowBandwidth:0  MessageLogging:0  ConnectType:{targetPlayer.ConnectionType}");
        }

        private void Command_sg_lag(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                targetPlayer = player;
            }

            _lagQuery.QueryPositionPing(targetPlayer, out PingSummary positionPing);
            _lagQuery.QueryClientPing(targetPlayer, out ClientPingSummary clientPing);
            _lagQuery.QueryReliablePing(targetPlayer, out PingSummary reliablePing);
            _lagQuery.QueryPacketloss(targetPlayer, out PacketlossSummary packetloss);

            int current = (positionPing.Current + clientPing.Current + 2 * reliablePing.Current) / 4;
            int average = (positionPing.Average + clientPing.Average + 2 * reliablePing.Average) / 4;
            int low = Math.Min(Math.Min(positionPing.Min, clientPing.Min), reliablePing.Min);
            int high = Math.Max(Math.Max(positionPing.Max, clientPing.Max), reliablePing.Max);

            _chat.SendMessage(player, $"PING Current:{current} ms  Average:{average} ms  Low:{low} ms  High:{high} ms  S2C:{packetloss.S2C * 100d,4:F1}%  C2S:{packetloss.C2S * 100d,4:F1}%  S2CWeapons:{packetloss.S2CWeapon * 100d,4:F1}%");
        }

        private void Command_sg_spec(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetPlayerTarget(out Player? targetPlayer))
            {
                _chat.SendMessage(player, "This command can only be sent to a player");
                return;
            }

            if (_game.HasLock(targetPlayer))
            {
                _game.Unlock(targetPlayer, false);
                _chat.SendMessage(player, "Player free to enter arena");
            }
            else
            {
                _game.Lock(targetPlayer, false, true, 0);
                _chat.SendMessage(player, "Player locked in spectator mode");
            }
        }

        private void Command_sg_lock(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetArenaTarget(out Arena? arena))
            {
                _chat.SendMessage(player, "This command can only be sent to an arena");
                return;
            }

            if (_game.HasLock(arena))
            {
                _game.UnlockArena(arena, false, false);
                _chat.SendMessage(player, "Arena UNLOCKED");
            }
            else
            {
                _game.LockArena(arena, false, false, false, false);
                _chat.SendMessage(player, "Arena LOCKED");
            }
        }

        private void Command_sg_scorereset(ReadOnlySpan<char> commandName, ReadOnlySpan<char> parameters, Player player, ITarget target)
        {
            if (!target.TryGetArenaTarget(out Arena? arena)
                && !target.TryGetTeamTarget(out arena, out _))
            {
                if (target.TryGetPlayerTarget(out Player? otherPlayer))
                {
                    arena = otherPlayer.Arena;
                }
            }

            if (arena is null)
                return;

            HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.TargetToSet(target, players);

                if (players.Count > 0)
                    return;

                foreach (Player otherPlayer in players)
                {
                    _scoreStats.ScoreReset(otherPlayer, PersistInterval.Reset);
                }

                _scoreStats.SendUpdates(arena, null);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(players);
            }
        }

        #endregion

        [ConfigHelp<bool>("SGCompat", "TemporarySet", ConfigScope.Global, Default = false,
            Description = """
                "If this setting is 0, the `?set` command will be mapped to `?seta`.
                "If this setting is 1, the `?set` command will be mapped to `?seta -t`.
                """)]
        private void ReadConfig()
        {
            bool temporarySet = _configManager.GetBool(_configManager.Global, "SGCompat", "TemporarySet", ConfigHelp.Constants.Global.SGCompat.TemporarySet.Default);
            _aliases.Remove("?set", out _);
            _aliases.Add("?set", temporarySet ? "seta -t" : "seta");
        }
    }
}

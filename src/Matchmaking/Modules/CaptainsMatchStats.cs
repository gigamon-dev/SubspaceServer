using Microsoft.IO;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Interfaces;
using System.Text.Json;

namespace SS.Matchmaking.Modules
{
    /// <summary>
    /// Module that tracks statistics for captains matches and saves results to the database.
    /// Requires <see cref="PostgreSqlGameStats"/> to be loaded for database persistence.
    /// </summary>
    [ModuleInfo($"""
        Tracks statistics for captains matches and saves results to the database.
        For use with the {nameof(CaptainsMatch)} module.
        Configure {nameof(CaptainsMatch)}.conf with: [CaptainsMatch] GameTypeId = <id>
        """)]
    public sealed class CaptainsMatchStats : IModule, ICaptainsMatchStatsBehavior
    {
        private static readonly RecyclableMemoryStreamManager s_memoryStreamManager = new();

        private readonly IChat _chat;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;

        // optional
        private IGameStatsRepository? _gameStatsRepository;

        private InterfaceRegistrationToken<ICaptainsMatchStatsBehavior>? _iCaptainsMatchStatsBehaviorToken;

        private string? _zoneServerName;

        /// <summary>Key: arena</summary>
        private readonly Dictionary<Arena, MatchContext> _activeMatches = [];

        public CaptainsMatchStats(
            IChat chat,
            IConfigManager configManager,
            ILogManager logManager)
        {
            _chat = chat ?? throw new ArgumentNullException(nameof(chat));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            _gameStatsRepository = broker.GetInterface<IGameStatsRepository>();

            if (_gameStatsRepository is not null)
            {
                _zoneServerName = _configManager.GetStr(_configManager.Global, "Billing", "ServerName");

                if (string.IsNullOrWhiteSpace(_zoneServerName))
                {
                    _logManager.LogM(LogLevel.Error, nameof(CaptainsMatchStats), "Missing setting, global.conf: Billing.ServerName");
                    broker.ReleaseInterface(ref _gameStatsRepository);
                    return false;
                }
            }

            _iCaptainsMatchStatsBehaviorToken = broker.RegisterInterface<ICaptainsMatchStatsBehavior>(this);
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iCaptainsMatchStatsBehaviorToken) != 0)
                return false;

            if (_gameStatsRepository is not null)
                broker.ReleaseInterface(ref _gameStatsRepository);

            return true;
        }

        #endregion

        #region ICaptainsMatchStatsBehavior

        [ConfigHelp<long>("CaptainsMatch", "GameTypeId", ConfigScope.Arena,
            Description = "The game type ID in the stats database that corresponds to this captains match configuration. Required for database persistence.")]
        void ICaptainsMatchStatsBehavior.MatchStarted(Arena arena, short freq1, IEnumerable<Player> team1, short freq2, IEnumerable<Player> team2)
        {
            var context = new MatchContext
            {
                StartTimestamp = DateTime.UtcNow,
                Team1 = new TeamInfo { Freq = freq1 },
                Team2 = new TeamInfo { Freq = freq2 },
            };

            foreach (Player p in team1)
            {
                context.Team1.PlayerNames.Add(p.Name!);
                context.PlayerStats[p.Name!] = new PlayerStats();
            }

            foreach (Player p in team2)
            {
                context.Team2.PlayerNames.Add(p.Name!);
                context.PlayerStats[p.Name!] = new PlayerStats();
            }

            _activeMatches[arena] = context;
        }

        void ICaptainsMatchStatsBehavior.PlayerKilled(Arena arena, Player killer, Player killed)
        {
            if (!_activeMatches.TryGetValue(arena, out MatchContext? context))
                return;

            if (context.PlayerStats.TryGetValue(killer.Name!, out PlayerStats? killerStats))
                killerStats.Kills++;

            if (context.PlayerStats.TryGetValue(killed.Name!, out PlayerStats? killedStats))
                killedStats.Deaths++;
        }

        async Task ICaptainsMatchStatsBehavior.MatchEndedAsync(Arena arena, short winnerFreq, short loserFreq)
        {
            if (!_activeMatches.Remove(arena, out MatchContext? context))
                return;

            context.EndTimestamp = DateTime.UtcNow;

            if (_gameStatsRepository is null)
                return;

            long? gameTypeId = ReadGameTypeId(arena);
            if (gameTypeId is null)
            {
                _logManager.LogA(LogLevel.Warn, nameof(CaptainsMatchStats), arena,
                    "CaptainsMatch.GameTypeId is not configured; match result not saved to database.");
                return;
            }

            try
            {
                using MemoryStream jsonStream = s_memoryStreamManager.GetStream();
                WriteMatchJson(jsonStream, arena, context, winnerFreq, gameTypeId.Value);
                jsonStream.Position = 0;

                long? gameId = await _gameStatsRepository.SaveGameAsync(jsonStream);

                if (gameId is not null)
                {
                    _logManager.LogA(LogLevel.Info, nameof(CaptainsMatchStats), arena, $"Saved captains match to database as game ID {gameId.Value}.");
                    _chat.SendArenaMessage(arena, $"Match stats saved (game ID {gameId.Value}).");
                }
                else
                {
                    _logManager.LogA(LogLevel.Warn, nameof(CaptainsMatchStats), arena, "Failed to save captains match to database.");
                }
            }
            catch (Exception ex)
            {
                _logManager.LogA(LogLevel.Error, nameof(CaptainsMatchStats), arena, $"Exception saving captains match stats: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        private long? ReadGameTypeId(Arena arena)
        {
            if (arena.Cfg is null)
                return null;

            int raw = _configManager.GetInt(arena.Cfg, "CaptainsMatch", "GameTypeId", -1);
            return raw < 0 ? null : (long)raw;
        }

        private void WriteMatchJson(Stream stream, Arena arena, MatchContext context, short winnerFreq, long gameTypeId)
        {
            using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { SkipValidation = false });

            writer.WriteStartObject();

            writer.WriteNumber("game_type_id"u8, gameTypeId);
            writer.WriteString("zone_server_name"u8, _zoneServerName);
            writer.WriteString("arena"u8, arena.Name);
            writer.WriteString("start_timestamp"u8, context.StartTimestamp);
            writer.WriteString("end_timestamp"u8, context.EndTimestamp!.Value);

            writer.WriteStartArray("team_stats"u8);

            WriteTeam(writer, context.Team1!, isWinner: context.Team1!.Freq == winnerFreq, context);
            WriteTeam(writer, context.Team2!, isWinner: context.Team2!.Freq == winnerFreq, context);

            writer.WriteEndArray(); // team_stats

            writer.WriteEndObject();
            writer.Flush();
        }

        private static void WriteTeam(Utf8JsonWriter writer, TeamInfo team, bool isWinner, MatchContext context)
        {
            writer.WriteStartObject();
            writer.WriteNumber("freq"u8, team.Freq);
            writer.WriteBoolean("is_winner"u8, isWinner);

            writer.WriteStartArray("player_slots"u8);

            foreach (string playerName in team.PlayerNames)
            {
                writer.WriteStartObject(); // slot
                writer.WriteStartArray("player_stats"u8);

                writer.WriteStartObject(); // player stat entry
                writer.WriteString("player"u8, playerName);

                context.PlayerStats.TryGetValue(playerName, out PlayerStats? stats);
                writer.WriteNumber("kills"u8, stats?.Kills ?? 0);
                writer.WriteNumber("deaths"u8, stats?.Deaths ?? 0);

                writer.WriteEndObject(); // player stat entry
                writer.WriteEndArray(); // player_stats
                writer.WriteEndObject(); // slot
            }

            writer.WriteEndArray(); // player_slots
            writer.WriteEndObject(); // team
        }

        #endregion

        #region Data

        private sealed class TeamInfo
        {
            public short Freq;
            public readonly List<string> PlayerNames = [];
        }

        private sealed class PlayerStats
        {
            public int Kills;
            public int Deaths;
        }

        private sealed class MatchContext
        {
            public DateTime StartTimestamp;
            public DateTime? EndTimestamp;
            public TeamInfo? Team1;
            public TeamInfo? Team2;
            public readonly Dictionary<string, PlayerStats> PlayerStats = new(StringComparer.OrdinalIgnoreCase);
        }

        #endregion
    }
}

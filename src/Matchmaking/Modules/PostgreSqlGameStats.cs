using Microsoft.Extensions.ObjectPool;
using Npgsql;
using NpgsqlTypes;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Interfaces;
using SS.Matchmaking.League;
using SS.Packets.Game;
using SS.Utilities.ObjectPool;
using System.Buffers;
using System.Collections.ObjectModel;
using System.Data;
using System.Text;
using System.Text.Json;

namespace SS.Matchmaking.Modules
{
    [ModuleInfo($"""
        Functionality to save game data into a PostgreSQL database.
        In global.conf, the SS.Matchmaking:DatabaseConnectionString setting is required.
        """)]
    public sealed class PostgreSqlGameStats(
        IChat chat,
        IConfigManager configManager,
        ILogManager logManager,
        IObjectPoolManager objectPoolManager,
        IPlayerData playerData) : IModule, IGameStatsRepository, ILeagueRepository, IDisposable
    {
        private readonly IChat _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        private readonly IConfigManager _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IObjectPoolManager _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        private readonly IPlayerData _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

        private InterfaceRegistrationToken<IGameStatsRepository>? _iGameStatsRepositoryToken;
        private InterfaceRegistrationToken<ILeagueRepository>? _iLeagueRepositoryToken;

        private NpgsqlDataSource? _dataSource;
        private bool _isDisposed;

        private readonly ObjectPool<List<string>> s_stringListPool = new DefaultObjectPool<List<string>>(new ListPooledObjectPolicy<string>() { InitialCapacity = Constants.TargetPlayerCount });

        #region Module members

        bool IModule.Load(IComponentBroker broker)
        {
            string? connectionString = _configManager.GetStr(_configManager.Global, "SS.Matchmaking", "DatabaseConnectionString");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logManager.LogM(LogLevel.Error, nameof(PostgreSqlGameStats), "Missing connection string (global.conf: SS.Matchmaking:DatabaseConnectionString).");
                return false;
            }

            _dataSource = NpgsqlDataSource.Create(connectionString);

            _iGameStatsRepositoryToken = broker.RegisterInterface<IGameStatsRepository>(this);
            _iLeagueRepositoryToken = broker.RegisterInterface<ILeagueRepository>(this);

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iGameStatsRepositoryToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iLeagueRepositoryToken) != 0)
                return false;

            if (_dataSource is not null)
            {
                _dataSource.Dispose();
                _dataSource = null;
            }

            return true;
        }

        #endregion

        #region IGameStatsRepository

        async Task<long?> IGameStatsRepository.SaveGameAsync(Stream jsonStream)
        {
            if (_dataSource is null)
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            try
            {
                await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
                NpgsqlCommand command = new("select ss.save_game_bytea($1)", connection);
                await using (command.ConfigureAwait(false))
                {
                    command.Parameters.AddWithValue(NpgsqlDbType.Bytea, jsonStream);
                    await command.PrepareAsync().ConfigureAwait(false);

                    var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        if (!await reader.ReadAsync().ConfigureAwait(false))
                            throw new Exception("Expected a row.");

                        return reader.GetInt64(0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(PostgreSqlGameStats), $"Error saving game to the database. {ex}");
                // TODO: add a fallback mechanism that saves the match json to a file to later send to the database as a retry?
                // would need something to periodically look for files and try to retry the save
                return null;
            }
        }

        async Task IGameStatsRepository.GetPlayerRatingsAsync(long gameTypeId, Dictionary<string, int> playerRatingDictionary)
        {
            ArgumentNullException.ThrowIfNull(playerRatingDictionary);

            if (playerRatingDictionary.Comparer != StringComparer.OrdinalIgnoreCase)
                throw new ArgumentException("Comparer must be StringComparer.OrdinalIgnoreCase.", nameof(playerRatingDictionary));

            if (_dataSource is null)
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            if (playerRatingDictionary.Count == 0)
                return;

            try
            {
                await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
                NpgsqlCommand command = new("select * from ss.get_player_rating($1,$2)", connection);
                await using (command.ConfigureAwait(false))
                {
                    // Using ArrayPool<string> is possible, but the array can be larger than needed.
                    // An ArraySegment can be passed as a parameter value, but it'll be boxed.
                    // So, it seems using a List from a pool is the only allocation free way.
                    List<string> playerNameList = s_stringListPool.Get();

                    try
                    {
                        command.Parameters.Add(new NpgsqlParameter<long>() { TypedValue = gameTypeId });

                        foreach (string playerName in playerRatingDictionary.Keys)
                            playerNameList.Add(playerName);

                        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Varchar, playerNameList);
                        await command.PrepareAsync().ConfigureAwait(false);

                        var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                        await using (reader.ConfigureAwait(false))
                        {
                            char[] playerNameArray = ArrayPool<char>.Shared.Rent(Constants.MaxPlayerNameLength);

                            try
                            {
                                int playerNameColumn = reader.GetOrdinal("player_name");
                                int ratingColumn = reader.GetOrdinal("rating");

                                while (await reader.ReadAsync().ConfigureAwait(false))
                                {
                                    string? playerName = GetPlayerName(reader, playerNameColumn, playerNameArray, playerNameList);
                                    if (playerName is null)
                                        continue;

                                    playerRatingDictionary[playerName] = reader.GetInt32(ratingColumn);
                                }
                            }
                            finally
                            {
                                ArrayPool<char>.Shared.Return(playerNameArray);
                            }
                        }
                    }
                    finally
                    {
                        s_stringListPool.Return(playerNameList);
                    }
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(PostgreSqlGameStats), $"Error getting player stats. {ex}");
            }


            // Local function that reads the player name from the DataReader without allocating a string, instead reusing the existing string instance.
            static string? GetPlayerName(NpgsqlDataReader reader, int ordinal, char[] buffer, List<string> playerNameList)
            {
                long charsRead = reader.GetChars(ordinal, 0, buffer, 0, Constants.MaxPlayerNameLength); // unfortunately, no overload for Span<char> so have to use a pooled char[] as the buffer
                ReadOnlySpan<char> playerNameSpan = buffer.AsSpan(0, (int)charsRead);

                foreach (string playerName in playerNameList)
                {
                    if (MemoryExtensions.Equals(playerName, playerNameSpan, StringComparison.OrdinalIgnoreCase))
                        return playerName;
                }

                return null;
            }
        }

        #endregion

        #region ILeagueRepository

        async Task<(GameStartStatus, LeagueGameInfo?)> ILeagueRepository.StartGameAsync(long seasonGameId, bool forceStart, CancellationToken cancellationToken)
        {
            if (_dataSource is null)
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            try
            {
                await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                NpgsqlCommand command = new("select * from league.start_game($1,$2);", connection);
                await using (command.ConfigureAwait(false))
                {
                    command.Parameters.Add(new NpgsqlParameter<long>() { TypedValue = seasonGameId });
                    command.Parameters.Add(new NpgsqlParameter<bool>() { TypedValue = forceStart });
                    await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

                    var dataReader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
                    await using (dataReader.ConfigureAwait(false))
                    {
                        if (!await dataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            throw new Exception("Expected a row.");

                        long statusCode = dataReader.GetInt64(0);

                        LeagueGameInfo? gameStartInfo;
                        if (await dataReader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false))
                        {
                            gameStartInfo = null;
                        }
                        else
                        {
                            Stream stream = await dataReader.GetStreamAsync(1, cancellationToken).ConfigureAwait(false);
                            await using (stream.ConfigureAwait(false))
                            {
                                gameStartInfo = await JsonSerializer.DeserializeAsync(stream, SourceGenerationContext.Default.LeagueGameInfo, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        return ((GameStartStatus)statusCode, gameStartInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(PostgreSqlGameStats), $"Error starting league game (seasonGameId:{seasonGameId}, force:{forceStart}). {ex}");
                throw;
            }
        }

        async Task<long?> ILeagueRepository.SaveGameAsync(long seasonGameId, Stream jsonStream)
        {
            if (_dataSource is null)
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            try
            {
                await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
                NpgsqlCommand command = new("select league.save_game_bytea($1,$2)", connection);
                await using (command.ConfigureAwait(false))
                {
                    command.Parameters.Add(new NpgsqlParameter<long>() { TypedValue = seasonGameId });
                    command.Parameters.AddWithValue(NpgsqlDbType.Bytea, jsonStream);
                    await command.PrepareAsync().ConfigureAwait(false);

                    var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        if (!await reader.ReadAsync().ConfigureAwait(false))
                            throw new Exception("Expected a row.");

                        return reader.GetInt64(0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(PostgreSqlGameStats), $"Error saving game to the database. {ex}");
                // TODO: add a fallback mechanism that saves the match json to a file to later send to the database as a retry?
                // would need something to periodically look for files and try to retry the save
                return null;
            }
        }

        // No ConfigureAwait in this method since it uses a Player object to send messages.
        async Task ILeagueRepository.PrintScheduleAsync(string playerName, long? seasonId, ReadOnlyDictionary<long, ILeagueMatch>? activeMatches)
        {
            if (_dataSource is null)
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            if (string.IsNullOrWhiteSpace(playerName))
                throw new ArgumentNullException(nameof(playerName));

            try
            {
                await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
                await using NpgsqlCommand command = new("select * from league.get_scheduled_games($1)", connection);

                if (seasonId is null)
                    command.Parameters.Add(new NpgsqlParameter() { Value = DBNull.Value });
                else
                    command.Parameters.Add(new NpgsqlParameter<long>() { TypedValue = seasonId.Value });

                await command.PrepareAsync();

                await using var reader = await command.ExecuteReaderAsync();

                int column_leagueId = reader.GetOrdinal("league_id");
                int column_leagueName = reader.GetOrdinal("league_name");
                int column_seasonId = reader.GetOrdinal("season_id");
                int column_seasonName = reader.GetOrdinal("season_name");
                int column_seasonGameId = reader.GetOrdinal("season_game_id");
                int column_gameTimestamp = reader.GetOrdinal("game_timestamp");
                int column_teams = reader.GetOrdinal("teams");
                int column_gameStatus = reader.GetOrdinal("game_status_id");

                bool isUnfiltered = seasonId is null;
                int count = 0;
                long? lastLeagueId = null;
                long? lastSeasonId = null;
                Player? player;

                while (await reader.ReadAsync())
                {
                    if (count++ == 0)
                    {
                        player = _playerData.FindPlayer(playerName); // Check that the player is still connected, between awaits.
                        if (player is null)
                            return;

                        _chat.SendMessage(player, $"     GameID Time                Teams                          Status");
                        _chat.SendMessage(player, $"----------- ------------------- ------------------------------ ------");
                    }

                    if (isUnfiltered)
                    {
                        // Check if we need to write the league name or season name.
                        long leagueId = reader.GetInt64(column_leagueId);
                        seasonId = reader.GetInt64(column_seasonId);
                        if (lastLeagueId != leagueId || lastSeasonId != seasonId)
                        {
                            lastLeagueId = leagueId;
                            lastSeasonId = seasonId;

                            char[] leagueName = ArrayPool<char>.Shared.Rent(128);
                            char[] seasonName = ArrayPool<char>.Shared.Rent(128);

                            try
                            {
                                long charsRead = reader.GetChars(column_leagueName, 0, leagueName, 0, leagueName.Length);
                                ReadOnlySpan<char> leagueSpan = new(leagueName, 0, (int)charsRead);

                                charsRead = reader.GetChars(column_seasonName, 0, seasonName, 0, seasonName.Length);
                                ReadOnlySpan<char> seasonSpan = new(seasonName, 0, (int)charsRead);

                                player = _playerData.FindPlayer(playerName); // Check that the player is still connected, between awaits.
                                if (player is null)
                                    return;

                                _chat.SendMessage(player, $"[ {leagueSpan} - {seasonSpan} ]");
                            }
                            finally
                            {
                                ArrayPool<char>.Shared.Return(leagueName);
                                ArrayPool<char>.Shared.Return(seasonName);
                            }
                        }
                    }

                    long seasonGameId = reader.GetInt64(column_seasonGameId);
                    DateTime? gameTimestamp = reader.IsDBNull(column_gameTimestamp) ? null : reader.GetDateTime(column_gameTimestamp);

                    char[] teams = ArrayPool<char>.Shared.Rent(ChatPacket.MaxMessageChars);
                    try
                    {
                        long charsRead = reader.GetChars(column_teams, 0, teams, 0, teams.Length);
                        ReadOnlySpan<char> teamsSpan = new(teams, 0, (int)charsRead);
                        GameStatus gameStatus = (GameStatus)reader.GetInt64(column_gameStatus);

                        StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();
                        try
                        {
                            sb.Append($"{seasonGameId,11}");

                            if (gameTimestamp is not null)
                            {
                                sb.Append($" {gameTimestamp:yyyy-MM-dd hh:mm:ss}");
                            }
                            else
                            {
                                sb.Append($" <not scheduled>    ");
                            }

                            sb.Append($" {teamsSpan,-30}");

                            switch (gameStatus)
                            {
                                case GameStatus.Pending:
                                    break;

                                case GameStatus.InProgress:
                                    sb.Append(" In Progress");

                                    if (activeMatches is not null && activeMatches.TryGetValue(seasonGameId, out ILeagueMatch? leagueMatch))
                                    {
                                        sb.Append($": ?go {leagueMatch.ArenaName}");
                                    }
                                    break;

                                case GameStatus.Complete:
                                    sb.Append(" Complete");
                                    break;

                                default:
                                    break;
                            }

                            player = _playerData.FindPlayer(playerName); // Check that the player is still connected, between awaits.
                            if (player is null)
                                return;

                            _chat.SendMessage(player, sb);
                        }
                        finally
                        {
                            _objectPoolManager.StringBuilderPool.Return(sb);
                        }
                    }
                    finally
                    {
                        ArrayPool<char>.Shared.Return(teams);
                    }
                }

                player = _playerData.FindPlayer(playerName); // Check that the player is still connected, between awaits.
                if (player is null)
                    return;

                if (count == 0)
                {
                    _chat.SendMessage(player, "No scheduled games.");
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(PostgreSqlGameStats), $"Error calling league.get_scheduled_games. {ex}");
                return;
            }
        }

        // No ConfigureAwait in this method since it uses a Player object to send messages.
        async Task ILeagueRepository.PrintStandingsAsync(string playerName, long seasonId)
        {
            if (_dataSource is null)
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            if (string.IsNullOrWhiteSpace(playerName))
                throw new ArgumentNullException(nameof(playerName));

            try
            {
                await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
                await using NpgsqlCommand command = new("select * from league.get_standings($1)", connection);

                command.Parameters.Add(new NpgsqlParameter<long>() { TypedValue = seasonId });
                await command.PrepareAsync();

                await using var reader = await command.ExecuteReaderAsync();

                int column_teamName = reader.GetOrdinal("team_name");
                int column_wins = reader.GetOrdinal("wins");
                int column_losses = reader.GetOrdinal("losses");
                int column_draws = reader.GetOrdinal("draws");

                char[] teamName = ArrayPool<char>.Shared.Rent(20);
                try
                {
                    int count = 0;
                    Player? player;

                    while (await reader.ReadAsync())
                    {
                        long charsRead = reader.GetChars(column_teamName, 0, teamName, 0, teamName.Length);

                        int? wins = reader.IsDBNull(column_wins) ? null : reader.GetInt32(column_wins);
                        int? losses = reader.IsDBNull(column_losses) ? null : reader.GetInt32(column_losses);
                        int? draws = reader.IsDBNull(column_draws) ? null : reader.GetInt32(column_draws);

                        ReadOnlySpan<char> teamNameSpan = new(teamName, 0, (int)charsRead);

                        player = _playerData.FindPlayer(playerName); // Check that the player is still connected, between awaits.
                        if (player is null)
                            return;

                        if (count++ == 0)
                        {
                            _chat.SendMessage(player, $"Team                 Wins Losses Draws");
                            _chat.SendMessage(player, $"-------------------- ---- ------ -----");
                        }

                        _chat.SendMessage(player, $"{teamNameSpan,-20} {wins,4} {losses,6} {draws,5}");
                    }

                    player = _playerData.FindPlayer(playerName); // Check that the player is still connected, between awaits.
                    if (player is null)
                        return;

                    if (count == 0)
                    {
                        _chat.SendMessage(player, "No standings found.");
                    }
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(teamName);
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(PostgreSqlGameStats), $"Error calling league.get_standings. {ex}");
                return;
            }
        }

        // No ConfigureAwait in this method since it uses a Player object to send messages.
        async Task ILeagueRepository.PrintResultsAsync(string playerName, long seasonId, string teamName)
        {
            if (_dataSource is null)
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            if (string.IsNullOrWhiteSpace(playerName))
                throw new ArgumentNullException(nameof(playerName));

            long? teamId = await GetTeamIdAsync(seasonId, teamName);
            if (teamId is null)
            {
                Player? player = _playerData.FindPlayer(playerName); // Check that the player is still connected, between awaits.
                if (player is null)
                    return;

                _chat.SendMessage(player, "Team not found.");
                return;
            }

            try
            {
                await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
                await using NpgsqlCommand command = new("select * from league.get_team_games($1)", connection);

                command.Parameters.Add(new NpgsqlParameter<long>() { TypedValue = teamId.Value });
                await command.PrepareAsync().ConfigureAwait(false);

                await using var reader = await command.ExecuteReaderAsync();

                //int column_roundNumber = reader.GetOrdinal("round_number");
                //int column_roundName = reader.GetOrdinal("round_name");
                int column_gameTimestamp = reader.GetOrdinal("game_timestamp");
                int column_gameId = reader.GetOrdinal("game_id");
                int column_teams = reader.GetOrdinal("teams");
                int column_winLoseDraw= reader.GetOrdinal("win_lose_draw");
                int column_scores = reader.GetOrdinal("scores");

                char[] teamsArray = ArrayPool<char>.Shared.Rent(ChatPacket.MaxMessageChars);
                char[] scoresArray = ArrayPool<char>.Shared.Rent(ChatPacket.MaxMessageChars);
                try
                {
                    int count = 0;
                    Player? player;

                    while (await reader.ReadAsync())
                    {
                        DateTime? gameTimestamp = reader.IsDBNull(column_gameTimestamp) ? null : reader.GetDateTime(column_gameTimestamp);
                        long? gameId = reader.IsDBNull(column_gameId) ? null : reader.GetInt64(column_gameId);
                        long charsRead = reader.GetChars(column_teams, 0, teamsArray, 0, teamsArray.Length);
                        ReadOnlySpan<char> teamsSpan = new(teamsArray, 0, (int)charsRead);
                        char? winLoseDraw = reader.IsDBNull(column_winLoseDraw) ? null : reader.GetChar(column_winLoseDraw);
                        string? winLoseDrawStr = winLoseDraw switch
                        {
                            'W' => "Win",
                            'L' => "Loss",
                            'D' => "Draw",
                            _ => null
                        };

                        ReadOnlySpan<char> scoresSpan = [];
                        if (!reader.IsDBNull(column_scores))
                        {
                            charsRead = reader.GetChars(column_scores, 0, scoresArray, 0, scoresArray.Length);
                            scoresSpan = new(scoresArray, 0, (int)charsRead);
                        }

                        player = _playerData.FindPlayer(playerName); // Check that the player is still connected, between awaits.
                        if (player is null)
                            return;

                        if (count++ == 0)
                        {
                            _chat.SendMessage(player, $"     GameID Date                Result Score      Teams");
                            _chat.SendMessage(player, $"----------- ------------------- ------ ---------- ------------------------------");
                        }

                        _chat.SendMessage(player, $"{gameId,11} {gameTimestamp,-19:yyyy-MM-dd HH:mm:ss} {winLoseDrawStr,-6} {scoresSpan,-10} {teamsSpan,-20}");
                    }

                    player = _playerData.FindPlayer(playerName); // Check that the player is still connected, between awaits.
                    if (player is null)
                        return;

                    if (count == 0)
                    {
                        _chat.SendMessage(player, "No results found.");
                    }
                    else if (count > 0)
                    {
                        _chat.SendMessage(player, $"----------- ------------------- ------ ---------- ------------------------------");
                    }
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(teamsArray);
                    ArrayPool<char>.Shared.Return(scoresArray);
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(PostgreSqlGameStats), $"Error calling league.get_team_games. {ex}");
                return;
            }
        }

        // No ConfigureAwait in this method since it uses a Player object to send messages.
        async Task ILeagueRepository.PrintRosterAsync(string playerName, long seasonId, string teamName)
        {
            if (_dataSource is null)
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            if (string.IsNullOrWhiteSpace(playerName))
                throw new ArgumentNullException(nameof(playerName));

            long? teamId = await GetTeamIdAsync(seasonId, teamName);
            if (teamId is null)
            {
                Player? player = _playerData.FindPlayer(playerName); // Check that the player is still connected, between awaits.
                if (player is null)
                    return;

                _chat.SendMessage(player, "Team not found.");
                return;
            }

            try
            {
                await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync();
                await using NpgsqlCommand command = new("select * from league.get_team_roster($1)", connection);

                command.Parameters.Add(new NpgsqlParameter<long>() { TypedValue = teamId.Value });
                command.Parameters.AddWithValue(teamName);
                await command.PrepareAsync().ConfigureAwait(false);

                await using var reader = await command.ExecuteReaderAsync();

                //int column_playerId = reader.GetOrdinal("player_id");
                int column_playerName = reader.GetOrdinal("player_name");
                int column_isCaptain = reader.GetOrdinal("is_captain");
                int column_isSuspended = reader.GetOrdinal("is_suspended");
                int column_enrollTimestamp = reader.GetOrdinal("enroll_timestamp");

                char[] nameArray = ArrayPool<char>.Shared.Rent(20);
                try
                {
                    int count = 0;
                    Player? player;
                    while (await reader.ReadAsync())
                    {
                        long charsRead = reader.GetChars(column_playerName, 0, nameArray, 0, nameArray.Length);
                        ReadOnlySpan<char> playerNameSpan = new(nameArray, 0, (int)charsRead);

                        bool isCaptain = reader.GetBoolean(column_isCaptain);
                        bool isSuspended = reader.GetBoolean(column_isSuspended);

                        DateTime? enrollTimestamp;
                        if (reader.IsDBNull(column_enrollTimestamp))
                            enrollTimestamp = null;
                        else
                            enrollTimestamp = reader.GetDateTime(column_enrollTimestamp);

                        player = _playerData.FindPlayer(playerName); // Check that the player is still connected, between awaits.
                        if (player is null)
                            return;

                        if (count++ == 0)
                        {
                            _chat.SendMessage(player, $"Player Name          Role Enrolled");
                            _chat.SendMessage(player, $"-------------------- ---- ----------");
                        }

                        _chat.SendMessage(player, $"{playerNameSpan,-20} {(isCaptain ? "CAP " : "    ")} {enrollTimestamp:yyyy-MM-dd} {(isSuspended ? " SUSPENDED" : "")}");
                    }

                    player = _playerData.FindPlayer(playerName); // Check that the player is still connected, between awaits.
                    if (player is null)
                        return;

                    if (count > 0)
                    {
                        _chat.SendMessage(player, $"-------------------- ---- ----------");
                    }
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(nameArray);
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(PostgreSqlGameStats), $"Error calling league.get_team_roster. {ex}");
                return;
            }
        }

        #endregion

        private async Task<long?> GetTeamIdAsync(long seasonId, string teamName)
        {
            if (_dataSource is null)
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            if (string.IsNullOrWhiteSpace(teamName))
                throw new ArgumentNullException(nameof(teamName));

            try
            {
                await using NpgsqlConnection connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
                NpgsqlCommand command = new("select league.get_team_id($1,$2)", connection);
                await using (command.ConfigureAwait(false))
                {
                    command.Parameters.Add(new NpgsqlParameter<long>() { TypedValue = seasonId });
                    command.Parameters.AddWithValue(teamName);

                    await command.PrepareAsync().ConfigureAwait(false);

                    var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        if (!reader.Read())
                            return null;

                        if (reader.IsDBNull(0))
                            return null;
                        else
                            return reader.GetInt64(0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(PostgreSqlGameStats), $"Error calling league.get_team_id. {ex}");
                return null;
            }
        }

        #region IDisposable

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _dataSource?.Dispose();
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}

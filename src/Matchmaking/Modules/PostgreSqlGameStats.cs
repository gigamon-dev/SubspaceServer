using Microsoft.Extensions.ObjectPool;
using Npgsql;
using NpgsqlTypes;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Interfaces;
using SS.Utilities.ObjectPool;
using System.Buffers;

namespace SS.Matchmaking.Modules
{
    [ModuleInfo($"""
        Functionality to save game data into a PostgreSQL database.
        In global.conf, the SS.Matchmaking:DatabaseConnectionString setting is required.
        """)]
    public sealed class PostgreSqlGameStats : IModule, IGameStatsRepository, IDisposable
    {
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private InterfaceRegistrationToken<IGameStatsRepository>? _iGameStatsRepositoryToken;

        private NpgsqlDataSource? _dataSource;
        private bool _isDisposed;

        private readonly ObjectPool<List<string>> s_stringListPool = new DefaultObjectPool<List<string>>(new ListPooledObjectPolicy<string>() { InitialCapacity = Constants.TargetPlayerCount });

        public PostgreSqlGameStats(
            IConfigManager configManager,
            ILogManager logManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        }

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
            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            broker.UnregisterInterface(ref _iGameStatsRepositoryToken);

            if (_dataSource is not null)
            {
                _dataSource.Dispose();
                _dataSource = null;
            }

            return true;
        }

        #endregion

        #region IGameStatsRepository

        public async Task<long?> SaveGameAsync(Stream jsonStream)
        {
            if (_dataSource is null)
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            try
            {
                NpgsqlCommand command = _dataSource.CreateCommand("select ss.save_game_bytea($1)");
                await using (command.ConfigureAwait(false))
                {
                    command.Parameters.AddWithValue(NpgsqlDbType.Bytea, jsonStream);
                    //await command.PrepareAsync().ConfigureAwait(false);

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

        public async Task GetPlayerRatingsAsync(long gameTypeId, Dictionary<string, int> playerRatingDictionary)
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
                NpgsqlCommand command = _dataSource.CreateCommand("select * from ss.get_player_rating($1,$2)");
                await using (command.ConfigureAwait(false))
                {
                    // Using ArrayPool<string> is possible, but the array can be larger than needed.
                    // An ArraySegment can be passed as a parameter value, but it'll be boxed.
                    // So, it seems using a List from a pool is the only allocation free way.
                    List<string> playerNameList = s_stringListPool.Get();

                    try
                    {
                        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, gameTypeId);

                        foreach (string playerName in playerRatingDictionary.Keys)
                            playerNameList.Add(playerName);

                        command.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Varchar, playerNameList);
                        //await command.PrepareAsync().ConfigureAwait(false);

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

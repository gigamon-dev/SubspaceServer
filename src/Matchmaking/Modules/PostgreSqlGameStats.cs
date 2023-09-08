using Npgsql;
using SS.Core;
using SS.Core.ComponentInterfaces;
using SS.Matchmaking.Interfaces;

namespace SS.Matchmaking.Modules
{
    [ModuleInfo($"""
        Functionality to save game data into a PostgreSQL database.
        In global.conf, the SS.Matchmaking:DatabaseConnectionString setting is required.
        """)]
    public class PostgreSqlGameStats : IModule, IGameStatsRepository
    {
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private InterfaceRegistrationToken<IGameStatsRepository> _iGameStatsRepositoryToken;
        
        private NpgsqlDataSource _dataSource;

        #region Module members

        public bool Load(
            ComponentBroker broker,
            IConfigManager configManager,
            ILogManager logManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

            string connectionString = configManager.GetStr(configManager.Global, "SS.Matchmaking", "DatabaseConnectionString");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                logManager.LogM(LogLevel.Error, nameof(PostgreSqlGameStats), "Missing connection string (global.conf: SS.Matchmaking:DatabaseConnectionString).");
                return false;
            }

            _dataSource = NpgsqlDataSource.Create(connectionString);
            _iGameStatsRepositoryToken = broker.RegisterInterface<IGameStatsRepository>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface(ref _iGameStatsRepositoryToken);

            return true;
        }

        #endregion

        #region IGameStatsRepository

        public async Task<long?> SaveGame(Stream jsonStream)
        {
            try
            {
                using NpgsqlCommand command = _dataSource.CreateCommand("select ss.save_game_bytea(@p_game_json_utf8_bytes)");
                command.Parameters.AddWithValue("p_game_json_utf8_bytes", jsonStream);

                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                if (!await reader.ReadAsync().ConfigureAwait(false))
                    throw new Exception("Expected a row.");

                return reader.GetInt64(0);
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(PostgreSqlGameStats), $"Error saving game to the database. {ex}");
                // TODO: add a fallback mechanism that saves the match json to a file to later send to the database as a retry?
                // would need something to periodically look for files and try to retry the save
                return null;
            }
        }

        #endregion
    }
}

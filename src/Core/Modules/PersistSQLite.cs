using Microsoft.Data.Sqlite;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that encapsulates database access for the <see cref="Persist"/> module.
    /// This implementation of <see cref="IPersistDatastore"/> uses a SQLite database as the storage mechanism.
    /// </summary>
    [CoreModuleInfo]
    public sealed class PersistSQLite : IModule, IPersistDatastore, IDisposable
    {
        private ILogManager _logManager;
        private IObjectPoolManager _objectPoolManager;
        private InterfaceRegistrationToken<IPersistDatastore> _iPersistDatastoreToken;

        private const string DatabasePath = "./data";
        private const string DatabaseFileName = "SS.Core.Modules.PersistSQLite.db";
        private const string ConnectionString = $"DataSource={DatabasePath}/{DatabaseFileName};Foreign Keys=True;Pooling=True";

        private static SqliteConnection _connection;
        private static readonly Dictionary<string, SqliteCommand> _commandDictionary = [];

        #region Module methods

        public bool Load(
            ComponentBroker broker,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            _iPersistDatastoreToken = broker.RegisterInterface<IPersistDatastore>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iPersistDatastoreToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IPersistDatastore

        bool IPersistDatastore.Open()
        {
            if (_connection is not null)
                return false;

            // Ensure the database directory exists.
            if (!Directory.Exists(DatabasePath))
            {
                try
                {
                    Directory.CreateDirectory(DatabasePath);

                    // TODO: Investigate setting folder permissions (at the moment I only see a Windows specific API)
                }
                catch (Exception ex)
                {
                    LogException($"Database directory '{DatabasePath}' does not exist and was unable to create it.", ex);
                    return false;
                }
            }

            bool initializeSchema = !File.Exists(Path.Combine(DatabasePath, DatabaseFileName));

            // Open the database connection.
            _connection = new(ConnectionString);
            _connection.Open();

            if (initializeSchema)
            {
                // Create tables, indexes, etc...
                try
                {
                    using SqliteTransaction transaction = _connection.BeginTransaction();

                    using (SqliteCommand command = _connection.CreateCommand())
                    {
                        command.CommandText = """
							CREATE TABLE [ArenaGroup](
							    [ArenaGroupId] INTEGER NOT NULL,
							    [ArenaGroup] TEXT NOT NULL UNIQUE COLLATE NOCASE,
							    PRIMARY KEY([ArenaGroupId] AUTOINCREMENT)
							);

							CREATE TABLE [ArenaGroupInterval](
							    [ArenaGroupIntervalId] INTEGER NOT NULL,
							    [ArenaGroupId] INTEGER NOT NULL,
							    [Interval] INTEGER NOT NULL,
							    [StartTimestamp] TEXT NOT NULL DEFAULT(datetime('now', 'utc')),
							    [EndTimestamp] TEXT NULL,
							    FOREIGN KEY([ArenaGroupId]) REFERENCES [ArenaGroup]([ArenaGroupId]),
							    PRIMARY KEY([ArenaGroupIntervalId] AUTOINCREMENT)
							);

							CREATE TABLE [CurrentArenaGroupInterval] (
								[ArenaGroupId] INTEGER NOT NULL,
								[Interval] INTEGER NOT NULL,
								[ArenaGroupIntervalId] INTEGER NOT NULL,
								FOREIGN KEY([ArenaGroupId]) REFERENCES [ArenaGroup]([ArenaGroupId]),
								FOREIGN KEY([ArenaGroupIntervalId]) REFERENCES [ArenaGroupInterval]([ArenaGroupIntervalId]),
								PRIMARY KEY([ArenaGroupId],[Interval])
							);

							CREATE TABLE [ArenaData] (
								[ArenaGroupIntervalId] INTEGER NOT NULL,
								[PersistKeyId] INTEGER NOT NULL,
								[Data] BLOB NOT NULL,
								FOREIGN KEY([ArenaGroupIntervalId]) REFERENCES [ArenaGroupInterval]([ArenaGroupIntervalId]),
								PRIMARY KEY([ArenaGroupIntervalId],[PersistKeyId])
							);

							CREATE TABLE [Player] (
								[PersistPlayerId] INTEGER NOT NULL,
								[PlayerName] TEXT NOT NULL UNIQUE COLLATE NOCASE,
								PRIMARY KEY([PersistPlayerId] AUTOINCREMENT)
							);

							CREATE TABLE [PlayerData](
							    [PersistPlayerId] INTEGER NOT NULL,
							    [ArenaGroupIntervalId] INTEGER NOT NULL,
							    [PersistKeyId] INTEGER NOT NULL,
							    [Data] BLOB NOT NULL,
							    FOREIGN KEY([ArenaGroupIntervalId]) REFERENCES [ArenaGroupInterval]([ArenaGroupIntervalId]),
							    FOREIGN KEY([PersistPlayerId]) REFERENCES [Player]([PersistPlayerId]),
							    PRIMARY KEY([PersistPlayerId], [ArenaGroupIntervalId], [PersistKeyId])
							);

							CREATE INDEX [IX_ArenaData_ArenaGroupIntervalId] ON [ArenaData] (
							    [ArenaGroupIntervalId]
							);

							CREATE INDEX [IX_ArenaGroupInterval_ArenaGroupId] ON [ArenaGroupInterval] (
							    [ArenaGroupId]
							);

							CREATE INDEX [IX_CurrentArenaGroupInterval_ArenaGroupId] ON [ArenaGroup] (
								[ArenaGroupId]
							);

							CREATE INDEX [IX_CurrentArenaGroupInterval_ArenaGroupIntervalId] ON [CurrentArenaGroupInterval] (
								[ArenaGroupIntervalId]
							);

							CREATE INDEX [IX_PlayerData_ArenaGroupIntervalId] ON [PlayerData] (
							    [ArenaGroupIntervalId]
							);

							CREATE INDEX [IX_PlayerData_PersistPlayerId] ON [PlayerData] (
							    [PersistPlayerId]
							);
							""";
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    LogException($"Error creating database.", ex);
                    return false;
                }
            }

            return true;
        }

        bool IPersistDatastore.Close()
        {
            if (_connection is null)
                return false;

            foreach ((_, SqliteCommand command) in _commandDictionary)
            {
                command.Dispose();
            }

            _commandDictionary.Clear();

            _connection.Dispose();
            _connection = null;

            return true;
        }

        bool IPersistDatastore.CreateArenaGroupIntervalAndMakeCurrent(string arenaGroup, PersistInterval interval)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arenaGroup);

            if (_connection is null)
                throw new InvalidOperationException("No connection. Use IPersistDatastore.Open first.");

            try
            {
                using SqliteTransaction transaction = _connection.BeginTransaction();
                DbCreateArenaGroupIntervalAndSetCurrent(transaction, arenaGroup, interval);
                transaction.Commit();

                return true;
            }
            catch (Exception ex)
            {
                LogException(
                    $"Error calling the database to create a new ArenaGroupInterval and set it as current for ArenaGroup {arenaGroup}, Interval {interval}.",
                    ex);

                return false;
            }
        }

        bool IPersistDatastore.GetPlayerData(Player player, string arenaGroup, PersistInterval interval, int key, Stream outStream)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentException.ThrowIfNullOrWhiteSpace(arenaGroup);
            ArgumentNullException.ThrowIfNull(outStream);

            try
            {
                using SqliteTransaction transaction = _connection.BeginTransaction();
                bool ret = DbGetPlayerData(transaction, player.Name, arenaGroup, interval, key, outStream);
                transaction.Commit();

                return ret;
            }
            catch (Exception ex)
            {
                LogException(
                    player,
                    $"Error getting player data from the database for ArenaGroup {arenaGroup}, Interval {interval}, Key {key}).",
                    ex);

                return false;
            }
        }

        bool IPersistDatastore.SetPlayerData(Player player, string arenaGroup, PersistInterval interval, int key, MemoryStream inStream)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentException.ThrowIfNullOrWhiteSpace(arenaGroup);
            ArgumentNullException.ThrowIfNull(inStream);

            try
            {
                using SqliteTransaction transaction = _connection.BeginTransaction();
                DbSetPlayerData(transaction, player.Name, arenaGroup, interval, key, inStream);
                transaction.Commit();

                return true;
            }
            catch (Exception ex)
            {
                LogException(
                    player,
                    $"Error saving player data to the database for ArenaGroup {arenaGroup}, Interval {interval}, Key {key}).",
                    ex);

                return false;
            }
        }

        bool IPersistDatastore.DeletePlayerData(Player player, string arenaGroup, PersistInterval interval, int key)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentException.ThrowIfNullOrWhiteSpace(arenaGroup);

            try
            {
                using SqliteTransaction transaction = _connection.BeginTransaction();
                DbDeletePlayerData(transaction, player.Name, arenaGroup, interval, key);
                transaction.Commit();

                return true;
            }
            catch (Exception ex)
            {
                LogException(
                    player,
                    $"Error deleting player data from the database for ArenaGroup {arenaGroup}, Interval {interval}, Key {key}).",
                    ex);

                return false;
            }
        }

        bool IPersistDatastore.GetArenaData(string arenaGroup, PersistInterval interval, int key, Stream outStream)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arenaGroup);
            ArgumentNullException.ThrowIfNull(outStream);

            try
            {
                using SqliteTransaction transaction = _connection.BeginTransaction();
                bool ret = DbGetArenaData(transaction, arenaGroup, interval, key, outStream);
                transaction.Commit();

                return ret;
            }
            catch (Exception ex)
            {
                LogException(
                    $"Error getting arena data from the database for ArenaGroup {arenaGroup}, Interval {interval}, Key {key}).",
                    ex);

                return false;
            }
        }

        bool IPersistDatastore.SetArenaData(string arenaGroup, PersistInterval interval, int key, MemoryStream inStream)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arenaGroup);
            ArgumentNullException.ThrowIfNull(inStream);

            try
            {
                using SqliteTransaction transaction = _connection.BeginTransaction();
                DbSetArenaData(transaction, arenaGroup, interval, key, inStream);
                transaction.Commit();

                return true;
            }
            catch (Exception ex)
            {
                LogException(
                    $"Error saving arena data to the database for ArenaGroup {arenaGroup}, Interval {interval}, Key {key}.",
                    ex);

                return false;
            }
        }

        bool IPersistDatastore.DeleteArenaData(string arenaGroup, PersistInterval interval, int key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arenaGroup);

            try
            {
                using SqliteTransaction transaction = _connection.BeginTransaction();
                DbDeleteArenaData(transaction, arenaGroup, interval, key);
                transaction.Commit();

                return true;
            }
            catch (Exception ex)
            {
                LogException(
                    $"Error deleting arena data from the database for ArenaGroup {arenaGroup}, Interval {interval}, Key {key}.",
                    ex);

                return false;
            }
        }

        bool IPersistDatastore.ResetGameInterval(string arenaGroup)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(arenaGroup);

            try
            {
                using SqliteTransaction transaction = _connection.BeginTransaction();
                DbResetGameInterval(transaction, arenaGroup);
                transaction.Commit();

                return true;
            }
            catch (Exception ex)
            {
                LogException(
                    $"Error resetting game interval for ArenaGroup {arenaGroup}.",
                    ex);

                return false;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            ((IPersistDatastore)this).Close();
        }

        #endregion

        #region Database procedures

        private static int DbGetOrCreateArenaGroupId(SqliteTransaction transaction, string arenaGroup)
        {
            try
            {
                const string sql = """
					SELECT ArenaGroupId
					FROM ArenaGroup
					WHERE ArenaGroup = @ArenaGroup
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@ArenaGroup", arenaGroup);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@ArenaGroup"].Value = arenaGroup;
                }

                try
                {
                    object arenaGroupIdObj = command.ExecuteScalar();
                    if (arenaGroupIdObj != null && arenaGroupIdObj != DBNull.Value)
                    {
                        return Convert.ToInt32(arenaGroupIdObj);
                    }
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error querying the ArenaGroup table for '{arenaGroup}'.", ex);
            }

            try
            {
                const string sql = """
					INSERT INTO ArenaGroup(ArenaGroup)
					VALUES(@ArenaGroup)
					RETURNING ArenaGroupId
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@ArenaGroup", arenaGroup);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@ArenaGroup"].Value = arenaGroup;
                }

                try
                {
                    object arenaGroupIdObj = command.ExecuteScalar();
                    if (arenaGroupIdObj != null && arenaGroupIdObj != DBNull.Value)
                    {
                        return Convert.ToInt32(arenaGroupIdObj);
                    }
                    else
                    {
                        throw new Exception("Insert did not return an ArenaGroupId.");
                    }
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error inserting into the ArenaGroup table for '{arenaGroup}'.", ex);
            }
        }

        private static int DbCreateArenaGroupIntervalAndSetCurrent(SqliteTransaction transaction, string arenaGroup, PersistInterval interval)
        {
            int arenaGroupId = DbGetOrCreateArenaGroupId(transaction, arenaGroup);

            DateTime now = DateTime.UtcNow; // For the EndTimestamp of the previous AccountGroupInterval AND the StartTimestamp of the new AccountGroupInterval to match.

            try
            {
                const string sql = """
                    UPDATE ArenaGroupInterval AS agi
                    SET EndTimestamp = @Now
                    FROM(
                        SELECT ArenaGroupIntervalId
                        FROM CurrentArenaGroupInterval as cagi
                        WHERE cagi.ArenaGroupId = @ArenaGroupId
                        	and cagi.Interval = @Interval
                    ) as c
                    WHERE agi.ArenaGroupIntervalId = c.ArenaGroupIntervalId
                    """;

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@ArenaGroupId", arenaGroupId);
                    command.Parameters.AddWithValue("@Interval", (int)interval);
                    command.Parameters.AddWithValue("@Now", now);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@ArenaGroupId"].Value = arenaGroupId;
                    command.Parameters["@Interval"].Value = (int)interval;
                    command.Parameters["@Now"].Value = now;
                }

                try
                {
                    command.ExecuteNonQuery();
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error setting the EndTimestamp of the previous ArenaGroupInterval for ArenaGroup '{arenaGroup}', Interval '{interval}'.", ex);
            }

            int arenaGroupIntervalId;

            try
            {
                const string sql = """
					INSERT INTO ArenaGroupInterval(
					     ArenaGroupId
					    ,Interval
					    ,StartTimestamp
					)
					VALUES(
					     @ArenaGroupId
					    ,@Interval
					    ,@Now
					)
					RETURNING ArenaGroupIntervalId
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@ArenaGroupId", arenaGroupId);
                    command.Parameters.AddWithValue("@Interval", (int)interval);
                    command.Parameters.AddWithValue("@Now", now);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@ArenaGroupId"].Value = arenaGroupId;
                    command.Parameters["@Interval"].Value = (int)interval;
                    command.Parameters["@Now"].Value = now;
                }

                try
                {
                    object arenaGroupIntervalIdObj = command.ExecuteScalar();
                    if (arenaGroupIntervalIdObj != null && arenaGroupIntervalIdObj != DBNull.Value)
                    {
                        arenaGroupIntervalId = Convert.ToInt32(arenaGroupIntervalIdObj);
                    }
                    else
                    {
                        throw new Exception("Insert did not return an ArenaGroupIntervalId.");
                    }
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error inserting into the ArenaGroupInterval table for ArenaGroup '{arenaGroup}', Interval '{interval}'.", ex);
            }

            try
            {
                const string sql = """
					UPDATE CurrentArenaGroupInterval
					SET ArenaGroupIntervalId = @ArenaGroupIntervalId
					WHERE ArenaGroupId = @ArenaGroupId
					    AND Interval = @Interval
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@ArenaGroupId", arenaGroupId);
                    command.Parameters.AddWithValue("@Interval", (int)interval);
                    command.Parameters.AddWithValue("@ArenaGroupIntervalId", arenaGroupIntervalId);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@ArenaGroupId"].Value = arenaGroupId;
                    command.Parameters["@Interval"].Value = (int)interval;
                    command.Parameters["@ArenaGroupIntervalId"].Value = arenaGroupIntervalId;
                }

                try
                {
                    int rowsAffected = command.ExecuteNonQuery();
                    if (rowsAffected == 1)
                        return arenaGroupIntervalId;
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error updating the CurrentArenaGroupInterval table for ArenaGroup '{arenaGroup}', Interval '{interval}'.", ex);
            }

            // no record in CurrentArenaGroupInterval yet, insert it
            try
            {
                const string sql = """
					INSERT INTO CurrentArenaGroupInterval(
					     ArenaGroupId
					    ,Interval
					    ,ArenaGroupIntervalId
					)
					VALUES(
					     @ArenaGroupId
					    ,@Interval
					    ,@ArenaGroupIntervalId
					)
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@ArenaGroupId", arenaGroupId);
                    command.Parameters.AddWithValue("@Interval", (int)interval);
                    command.Parameters.AddWithValue("@ArenaGroupIntervalId", arenaGroupIntervalId);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@ArenaGroupId"].Value = arenaGroupId;
                    command.Parameters["@Interval"].Value = (int)interval;
                    command.Parameters["@ArenaGroupIntervalId"].Value = arenaGroupIntervalId;
                }

                try
                {
                    command.ExecuteNonQuery();
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error inserting into the CurrentArenaGroupInterval table for ArenaGroup '{arenaGroup}', Interval '{interval}'.", ex);
            }

            return arenaGroupIntervalId;
        }

        private static int? DbGetCurrentArenaGroupIntervalId(SqliteTransaction transaction, string arenaGroup, PersistInterval interval)
        {
            try
            {
                const string sql = """
					SELECT cagi.ArenaGroupIntervalId
					FROM ArenaGroup AS ag
					INNER JOIN CurrentArenaGroupInterval as cagi
					    ON ag.ArenaGroupId = cagi.ArenaGroupId
					WHERE ag.ArenaGroup = @ArenaGroup
					    AND cagi.Interval = @Interval
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@ArenaGroup", arenaGroup);
                    command.Parameters.AddWithValue("@Interval", interval);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@ArenaGroup"].Value = arenaGroup;
                    command.Parameters["@Interval"].Value = interval;
                }

                try
                {
                    object arenaGroupIntervalIdObj = command.ExecuteScalar();
                    if (arenaGroupIntervalIdObj != null && arenaGroupIntervalIdObj != DBNull.Value)
                    {
                        return Convert.ToInt32(arenaGroupIntervalIdObj);
                    }
                }
                finally
                {
                    ResetPreparedCommand(command);
                }

                return null;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error querying the CurrentArenaGroupInterval table for ArenaGroup '{arenaGroup}', Interval '{interval}'.", ex);
            }
        }

        private static int DbGetOrCreateCurrentArenaGroupIntervalId(SqliteTransaction transaction, string arenaGroup, PersistInterval interval)
        {
            int? arenaGroupIntervalId = DbGetCurrentArenaGroupIntervalId(transaction, arenaGroup, interval);
            if (arenaGroupIntervalId != null)
                return arenaGroupIntervalId.Value;

            return DbCreateArenaGroupIntervalAndSetCurrent(transaction, arenaGroup, interval);
        }

        private static int DbGetOrCreatePersistPlayerId(SqliteTransaction transaction, string playerName)
        {
            try
            {
                const string sql = """
					SELECT PersistPlayerId 
					FROM Player 
					WHERE PlayerName = @PlayerName
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@PlayerName", playerName);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@PlayerName"].Value = playerName;
                }

                try
                {
                    object persistPlayerIdObj = command.ExecuteScalar();
                    if (persistPlayerIdObj != null && persistPlayerIdObj != DBNull.Value)
                    {
                        return Convert.ToInt32(persistPlayerIdObj);
                    }
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error querying the Player table for player name '{playerName}'.", ex);
            }

            try
            {
                const string sql = """
					INSERT INTO Player(PlayerName)
					VALUES(@PlayerName)
					RETURNING PersistPlayerId
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@PlayerName", playerName);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@PlayerName"].Value = playerName;
                }

                try
                {
                    object persistPlayerIdObj = command.ExecuteScalar();
                    if (persistPlayerIdObj != null && persistPlayerIdObj != DBNull.Value)
                    {
                        return Convert.ToInt32(persistPlayerIdObj);
                    }
                    else
                    {
                        throw new Exception("Error inserting into the Player table.");
                    }
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error inserting into the Player table for player name '{playerName}'.", ex);
            }
        }

        private static bool DbGetPlayerData(SqliteTransaction transaction, string playerName, string arenaGroup, PersistInterval interval, int persistKey, Stream outStream)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(transaction, arenaGroup, interval);

            int persistPlayerId = DbGetOrCreatePersistPlayerId(transaction, playerName);

            try
            {
                const string sql = """
					SELECT
					     rowid
					    ,Data
					FROM PlayerData
					WHERE PersistPlayerId = @PersistPlayerId
					    AND ArenaGroupIntervalId = @ArenaGroupIntervalId
					    AND PersistKeyId = @PersistKeyId
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@PersistPlayerId", persistPlayerId);
                    command.Parameters.AddWithValue("@ArenaGroupIntervalId", arenaGroupIntervalId);
                    command.Parameters.AddWithValue("@PersistKeyId", persistKey);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@PersistPlayerId"].Value = persistPlayerId;
                    command.Parameters["@ArenaGroupIntervalId"].Value = arenaGroupIntervalId;
                    command.Parameters["@PersistKeyId"].Value = persistKey;
                }

                try
                {
                    using SqliteDataReader reader = command.ExecuteReader();

                    if (reader.Read())
                    {
                        using Stream blobStream = reader.GetStream(1);
                        blobStream.CopyTo(outStream);
                        return true;
                    }

                    return false;
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error querying the PlayerData table for PersistPlayerId {persistPlayerId}, ArenaGroupIntervalId {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        private static void DbSetPlayerData(SqliteTransaction transaction, string playerName, string arenaGroup, PersistInterval interval, int persistKey, MemoryStream dataStream)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(transaction, arenaGroup, interval);

            int persistPlayerId = DbGetOrCreatePersistPlayerId(transaction, playerName);

            try
            {
                const string sql = """
					INSERT OR REPLACE INTO PlayerData(
					     PersistPlayerId
					    ,ArenaGroupIntervalId
					    ,PersistKeyId
					    ,Data
					)
					VALUES(
					     @PersistPlayerId
					    ,@ArenaGroupIntervalId
					    ,@PersistKeyId
					    ,zeroblob(@DataLength)
					);

					SELECT last_insert_rowid();
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@PersistPlayerId", persistPlayerId);
                    command.Parameters.AddWithValue("@ArenaGroupIntervalId", arenaGroupIntervalId);
                    command.Parameters.AddWithValue("@PersistKeyId", persistKey);
                    command.Parameters.AddWithValue("@DataLength", dataStream.Length);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@PersistPlayerId"].Value = persistPlayerId;
                    command.Parameters["@ArenaGroupIntervalId"].Value = arenaGroupIntervalId;
                    command.Parameters["@PersistKeyId"].Value = persistKey;
                    command.Parameters["@DataLength"].Value = dataStream.Length;
                }

                try
                {
                    long rowId = (long)command.ExecuteScalar();

                    // Write the blob.
                    using SqliteBlob blob = new(_connection, "PlayerData", "Data", rowId);
                    dataStream.CopyTo(blob);
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error inserting into the PlayerData table for PersistPlayerId {persistPlayerId}, ArenaGroupIntervalId {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        private static void DbDeletePlayerData(SqliteTransaction transaction, string playerName, string arenaGroup, PersistInterval interval, int persistKey)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(transaction, arenaGroup, interval);

            int persistPlayerId = DbGetOrCreatePersistPlayerId(transaction, playerName);

            try
            {
                const string sql = """
					DELETE FROM PlayerData
					WHERE PersistPlayerId = @PersistPlayerId
					    AND ArenaGroupIntervalId = @ArenaGroupIntervalId
					    AND PersistKeyId = @PersistKeyId
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@PersistPlayerId", persistPlayerId);
                    command.Parameters.AddWithValue("@ArenaGroupIntervalId", arenaGroupIntervalId);
                    command.Parameters.AddWithValue("@PersistKeyId", persistKey);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@PersistPlayerId"].Value = persistPlayerId;
                    command.Parameters["@ArenaGroupIntervalId"].Value = arenaGroupIntervalId;
                    command.Parameters["@PersistKeyId"].Value = persistKey;
                }

                try
                {
                    command.ExecuteNonQuery();
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error deleting from PlayerData for PersistPlayerId {persistPlayerId}, ArenaGroupIntervalId {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        private static bool DbGetArenaData(SqliteTransaction transaction, string arenaGroup, PersistInterval interval, int persistKey, Stream outStream)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(transaction, arenaGroup, interval);

            try
            {
                const string sql = """
					SELECT
					     rowid
					    ,Data
					FROM ArenaData
					WHERE ArenaGroupIntervalId = @ArenaGroupIntervalId
					    AND PersistKeyId = @PersistKeyId
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@ArenaGroupIntervalId", arenaGroupIntervalId);
                    command.Parameters.AddWithValue("@PersistKeyId", persistKey);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@ArenaGroupIntervalId"].Value = arenaGroupIntervalId;
                    command.Parameters["@PersistKeyId"].Value = persistKey;
                }

                try
                {
                    using SqliteDataReader reader = command.ExecuteReader();

                    if (reader.Read())
                    {
                        using var blobStream = reader.GetStream(1);
                        blobStream.CopyTo(outStream);
                        return true;
                    }

                    return false;
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error querying the ArenaData table for ArenaGroupInterval {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        private static void DbSetArenaData(SqliteTransaction transaction, string arenaGroup, PersistInterval interval, int persistKey, MemoryStream dataStream)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(transaction, arenaGroup, interval);

            try
            {
                const string sql = """
					INSERT OR REPLACE INTO ArenaData(
					     ArenaGroupIntervalId
					    ,PersistKeyId
					    ,Data
					)
					VALUES(
					     @ArenaGroupIntervalId
					    ,@PersistKeyId
					    ,zeroblob(@DataLength)
					);

					SELECT last_insert_rowid();
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@ArenaGroupIntervalId", arenaGroupIntervalId);
                    command.Parameters.AddWithValue("@PersistKeyId", persistKey);
                    command.Parameters.AddWithValue("@DataLength", dataStream.Length);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@ArenaGroupIntervalId"].Value = arenaGroupIntervalId;
                    command.Parameters["@PersistKeyId"].Value = persistKey;
                    command.Parameters["@DataLength"].Value = dataStream.Length;
                }

                try
                {
                    long rowId = (long)command.ExecuteScalar();

                    // Write the blob.
                    using SqliteBlob blob = new(_connection, "ArenaData", "Data", rowId);
                    dataStream.CopyTo(blob);
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error inserting into the ArenaData table for ArenaGroupIntervalId {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        private static void DbDeleteArenaData(SqliteTransaction transaction, string arenaGroup, PersistInterval interval, int persistKey)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(transaction, arenaGroup, interval);

            try
            {
                const string sql = """
					DELETE FROM ArenaData
					WHERE ArenaGroupIntervalId = @ArenaGroupIntervalId
					    AND PersistKeyId = @PersistKeyId
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@ArenaGroupIntervalId", arenaGroupIntervalId);
                    command.Parameters.AddWithValue("@PersistKeyId", persistKey);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@ArenaGroupIntervalId"].Value = arenaGroupIntervalId;
                    command.Parameters["@PersistKeyId"].Value = persistKey;
                }

                try
                {
                    command.ExecuteNonQuery();
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error deleting from the ArenaGroup table for ArenaGroupIntervalId {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        private static void DbResetGameInterval(SqliteTransaction transaction, string arenaGroup)
        {
            int? arenaGroupIntervalId = DbGetCurrentArenaGroupIntervalId(transaction, arenaGroup, PersistInterval.Game);
            if (arenaGroupIntervalId == null)
                return;

            try
            {
                const string sql = """
					DELETE FROM ArenaData
					WHERE ArenaGroupIntervalId = @ArenaGroupIntervalId
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@ArenaGroupIntervalId", arenaGroupIntervalId.Value);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@ArenaGroupIntervalId"].Value = arenaGroupIntervalId.Value;
                }

                try
                {
                    command.ExecuteNonQuery();
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error deleting from the ArenaGroup table for ArenaGroupIntervalId {arenaGroupIntervalId.Value}.", ex);
            }

            try
            {
                const string sql = """
					DELETE FROM PlayerData
					WHERE ArenaGroupIntervalId = @ArenaGroupIntervalId
					""";

                if (!_commandDictionary.TryGetValue(sql, out SqliteCommand command))
                {
                    command = _connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("@ArenaGroupIntervalId", arenaGroupIntervalId.Value);

                    _commandDictionary.Add(sql, command);
                }
                else
                {
                    command.Transaction = transaction;
                    command.Parameters["@ArenaGroupIntervalId"].Value = arenaGroupIntervalId.Value;
                }

                try
                {
                    command.ExecuteNonQuery();
                }
                finally
                {
                    ResetPreparedCommand(command);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error deleting from the PlayerData table for ArenaGroupIntervalId {arenaGroupIntervalId.Value}.", ex);
            }
        }

        #endregion

        /// <summary>
        /// Resets a command that's intended to be reused (internally has a prepared statement).
        /// </summary>
        /// <param name="command">The command to reset.</param>
        private static void ResetPreparedCommand(SqliteCommand command)
        {
            if (command is null)
                return;

            // Clear the parameter values.
            for (int index = 0; index < command.Parameters.Count; index++)
            {
                command.Parameters[index].Value = null;
            }

            // Remove the transaction from the command.
            command.Transaction = null;
        }

        #region Logging helper methods

        // TODO: Add methods to LogManager for logging exceptions.
        private void LogException(string message, Exception ex)
        {
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    sb.Append(message);
                }

                // TODO: Add a setting to log the full exception (stack trace and all)

                while (ex != null)
                {
                    sb.Append(' ');
                    sb.Append(ex.Message);

                    ex = ex.InnerException;
                }

                if (sb.Length > 0)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Persist), sb);
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        private void LogException(Player player, string message, Exception ex)
        {
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    sb.Append(message);
                }

                // TODO: Add a setting to log the full exception (stack trace and all)

                while (ex != null)
                {
                    sb.Append(' ');
                    sb.Append(ex.Message);

                    ex = ex.InnerException;
                }

                if (sb.Length > 0)
                {
                    _logManager.LogP(LogLevel.Error, nameof(Persist), player, sb);
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        private void LogException(Arena arena, string message, Exception ex)
        {
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    sb.Append(message);
                }

                // TODO: Add a setting to log the full exception (stack trace and all)

                while (ex != null)
                {
                    sb.Append(' ');
                    sb.Append(ex.Message);

                    ex = ex.InnerException;
                }

                if (sb.Length > 0)
                {
                    _logManager.LogA(LogLevel.Error, nameof(Persist), arena, sb);
                }
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        #endregion
    }
}

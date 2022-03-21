using SS.Core.ComponentInterfaces;
using System;
using System.Data.SQLite;
using System.IO;
using System.Text;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that encapsulates database access for the <see cref="Modules.Persist"/> module.
    /// This implementation of <see cref="IPersistDatastore"/> uses a SQLite database as the storage mechanism.
    /// </summary>
    public class PersistSQLite : IModule, IPersistDatastore
    {
        private ILogManager _logManager;
        private IObjectPoolManager _objectPoolManager;
        private InterfaceRegistrationToken<IPersistDatastore> _iPersistDatastore;

        private const string DatabasePath = "./data";
        private const string DatabaseFileName = "SS.Core.Modules.PersistSQLite.db";
        private const string ConnectionString = $"Data Source={DatabasePath}/{DatabaseFileName};Version=3;";

        #region Module methods

        public bool Load(
            ComponentBroker broker,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));

            _iPersistDatastore = broker.RegisterInterface<IPersistDatastore>(this);
            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iPersistDatastore) != 0)
                return false;

            return true;
        }

        #endregion

        #region IPersistDatastore

        bool IPersistDatastore.Open()
        {
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

            if (!File.Exists(Path.Combine(DatabasePath, DatabaseFileName)))
            {
                // create tables, etc...
                try
                {
                    using SQLiteConnection conn = new(ConnectionString);
                    conn.Open();

                    using SQLiteTransaction transaction = conn.BeginTransaction();

                    // create tables
                    using (SQLiteCommand command = conn.CreateCommand())
                    {
                        command.CommandText = @"
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
	[ArenaGroupId]INTEGER NOT NULL,
	[Interval] INTEGER NOT NULL,
	[ArenaGroupIntervalId] INTEGER NOT NULL,
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
";
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
            // no-op
            return true;
        }

        bool IPersistDatastore.CreateArenaGroupIntervalAndMakeCurrent(string arenaGroup, PersistInterval interval)
        {
            if (string.IsNullOrWhiteSpace(arenaGroup))
                throw new ArgumentException("Cannot be null or white-space.", nameof(arenaGroup));

            try
            {
                using SQLiteConnection conn = new(ConnectionString);
                conn.Open();

                using SQLiteTransaction transaction = conn.BeginTransaction();
                DbCreateArenaGroupIntervalAndSetCurrent(conn, arenaGroup, interval);
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
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (string.IsNullOrWhiteSpace(arenaGroup))
                throw new ArgumentException("Cannot be null or white-space.", nameof(arenaGroup));

            if (outStream == null)
                throw new ArgumentNullException(nameof(outStream));

            try
            {
                using SQLiteConnection conn = new(ConnectionString);
                conn.Open();

                using SQLiteTransaction transaction = conn.BeginTransaction();
                bool ret = DbGetPlayerData(conn, player.Name, arenaGroup, interval, key, outStream);
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
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (string.IsNullOrWhiteSpace(arenaGroup))
                throw new ArgumentException("Cannot be null or white-space.", nameof(arenaGroup));

            if (inStream == null)
                throw new ArgumentNullException(nameof(inStream));

            try
            {
                using SQLiteConnection conn = new(ConnectionString);
                conn.Open();

                using SQLiteTransaction transaction = conn.BeginTransaction();
                DbSetPlayerData(conn, player.Name, arenaGroup, interval, key, inStream);
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
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (string.IsNullOrWhiteSpace(arenaGroup))
                throw new ArgumentException("Cannot be null or white-space.", nameof(arenaGroup));

            try
            {
                using SQLiteConnection conn = new(ConnectionString);
                conn.Open();

                using SQLiteTransaction transaction = conn.BeginTransaction();
                DbDeletePlayerData(conn, player.Name, arenaGroup, interval, key);
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
            if (string.IsNullOrWhiteSpace(arenaGroup))
                throw new ArgumentException("Cannot be null or white-space.", nameof(arenaGroup));

            if (outStream == null)
                throw new ArgumentNullException(nameof(outStream));

            try
            {
                using SQLiteConnection conn = new(ConnectionString);
                conn.Open();

                using SQLiteTransaction transaction = conn.BeginTransaction();
                bool ret = DbGetArenaData(conn, arenaGroup, interval, key, outStream);
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
            if (string.IsNullOrWhiteSpace(arenaGroup))
                throw new ArgumentException("Cannot be null or white-space.", nameof(arenaGroup));

            if (inStream == null)
                throw new ArgumentNullException(nameof(inStream));

            try
            {
                using SQLiteConnection conn = new(ConnectionString);
                conn.Open();

                using SQLiteTransaction transaction = conn.BeginTransaction();
                DbSetArenaData(conn, arenaGroup, interval, key, inStream);
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
            if (string.IsNullOrWhiteSpace(arenaGroup))
                throw new ArgumentException("Cannot be null or white-space.", nameof(arenaGroup));

            try
            {
                using SQLiteConnection conn = new(ConnectionString);
                conn.Open();

                using SQLiteTransaction transaction = conn.BeginTransaction();
                DbDeleteArenaData(conn, arenaGroup, interval, key);
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

        #endregion

        private static int DbGetOrCreateArenaGroupId(SQLiteConnection conn, string arenaGroup)
        {
            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
SELECT ArenaGroupId
FROM ArenaGroup
WHERE ArenaGroup = @ArenaGroup";
                command.Parameters.AddWithValue("ArenaGroup", arenaGroup);

                object arenaGroupIdObj = command.ExecuteScalar();
                if (arenaGroupIdObj != null && arenaGroupIdObj != DBNull.Value)
                {
                    return Convert.ToInt32(arenaGroupIdObj);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error querying the ArenaGroup table for '{arenaGroup}'.", ex);
            }

            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
INSERT INTO ArenaGroup(ArenaGroup)
VALUES(@ArenaGroup)
RETURNING ArenaGroupId";
                command.Parameters.AddWithValue("ArenaGroup", arenaGroup);

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
            catch (Exception ex)
            {
                throw new Exception($"Error inserting into the ArenaGroup table for '{arenaGroup}'.", ex);
            }
        }

        private static int DbCreateArenaGroupIntervalAndSetCurrent(SQLiteConnection conn, string arenaGroup, PersistInterval interval)
        {
            int arenaGroupId = DbGetOrCreateArenaGroupId(conn, arenaGroup);

            DateTime now = DateTime.UtcNow; // For the EndTimestamp of the previous AccountGroupInterval AND the StartTimestamp of the new AccountGroupInterval to match.

            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
UPDATE ArenaGroupInterval AS agi
SET EndTimestamp = @Now
FROM(
	SELECT ArenaGroupIntervalId
	FROM CurrentArenaGroupInterval as cagi
	WHERE cagi.ArenaGroupId = @ArenaGroupId
		and cagi.Interval = @Interval
) as c
WHERE agi.ArenaGroupIntervalId = c.ArenaGroupIntervalId";
                command.Parameters.AddWithValue("ArenaGroupId", arenaGroupId);
                command.Parameters.AddWithValue("Interval", (int)interval);
                command.Parameters.AddWithValue("Now", now);

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error setting the EndTimestamp of the previous ArenaGroupInterval for ArenaGroup '{arenaGroup}', Interval '{interval}'.", ex);
            }

            int arenaGroupIntervalId;

            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
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
RETURNING ArenaGroupIntervalId";
                command.Parameters.AddWithValue("ArenaGroupId", arenaGroupId);
                command.Parameters.AddWithValue("Interval", (int)interval);
                command.Parameters.AddWithValue("Now", now);

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
            catch (Exception ex)
            {
                throw new Exception($"Error inserting into the ArenaGroupInterval table for ArenaGroup '{arenaGroup}', Interval '{interval}'.", ex);
            }

            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
UPDATE CurrentArenaGroupInterval
SET ArenaGroupIntervalId = @ArenaGroupIntervalId
WHERE ArenaGroupId = @ArenaGroupId
    AND Interval = @Interval";
                command.Parameters.AddWithValue("ArenaGroupId", arenaGroupId);
                command.Parameters.AddWithValue("Interval", (int)interval);
                command.Parameters.AddWithValue("ArenaGroupIntervalId", arenaGroupIntervalId);

                int rowsAffected = command.ExecuteNonQuery();
                if (rowsAffected == 1)
                    return arenaGroupIntervalId;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error updating the CurrentArenaGroupInterval table for ArenaGroup '{arenaGroup}', Interval '{interval}'.", ex);
            }

            // no record in CurrentArenaGroupInterval yet, insert it
            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
INSERT INTO CurrentArenaGroupInterval(
     ArenaGroupId
    ,Interval
    ,ArenaGroupIntervalId
)
VALUES(
     @ArenaGroupId
    ,@Interval
    ,@ArenaGroupIntervalId
)";
                command.Parameters.AddWithValue("ArenaGroupId", arenaGroupId);
                command.Parameters.AddWithValue("Interval", (int)interval);
                command.Parameters.AddWithValue("ArenaGroupIntervalId", arenaGroupIntervalId);

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error inserting into the CurrentArenaGroupInterval table for ArenaGroup '{arenaGroup}', Interval '{interval}'.", ex);
            }

            return arenaGroupIntervalId;
        }

        private static int DbGetOrCreateCurrentArenaGroupIntervalId(SQLiteConnection conn, string arenaGroup, PersistInterval interval)
        {
            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
SELECT cagi.ArenaGroupIntervalId
FROM ArenaGroup AS ag
INNER JOIN CurrentArenaGroupInterval as cagi
    ON ag.ArenaGroupId = cagi.ArenaGroupId
WHERE ag.ArenaGroup = @ArenaGroup
    AND cagi.Interval = @Interval";
                command.Parameters.AddWithValue("ArenaGroup", arenaGroup);
                command.Parameters.AddWithValue("Interval", interval);

                object arenaGroupIntervalIdObj = command.ExecuteScalar();
                if (arenaGroupIntervalIdObj != null && arenaGroupIntervalIdObj != DBNull.Value)
                {
                    return Convert.ToInt32(arenaGroupIntervalIdObj);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error querying the CurrentArenaGroupInterval table for ArenaGroup '{arenaGroup}', Interval '{interval}'.", ex);
            }

            return DbCreateArenaGroupIntervalAndSetCurrent(conn, arenaGroup, interval);
        }

        private static int DbGetOrCreatePersistPlayerId(SQLiteConnection conn, string playerName)
        {
            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
SELECT PersistPlayerId 
FROM Player 
WHERE PlayerName = @PlayerName";
                command.Parameters.AddWithValue("PlayerName", playerName);

                object persistPlayerIdObj = command.ExecuteScalar();
                if (persistPlayerIdObj != null && persistPlayerIdObj != DBNull.Value)
                {
                    return Convert.ToInt32(persistPlayerIdObj);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error querying the Player table for player name '{playerName}'.", ex);
            }

            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
INSERT INTO Player(PlayerName)
VALUES(@PlayerName)
RETURNING PersistPlayerId";
                command.Parameters.AddWithValue("PlayerName", playerName);

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
            catch (Exception ex)
            {
                throw new Exception($"Error inserting into the Player table for player name '{playerName}'.", ex);
            }
        }

        private static bool DbGetPlayerData(SQLiteConnection conn, string playerName, string arenaGroup, PersistInterval interval, int persistKey, Stream outStream)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(conn, arenaGroup, interval);

            int persistPlayerId = DbGetOrCreatePersistPlayerId(conn, playerName);

            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
SELECT Data
FROM PlayerData
WHERE PersistPlayerId = @PersistPlayerId
    AND ArenaGroupIntervalId = @ArenaGroupIntervalId
    AND PersistKeyId = @PersistKeyId";
                command.Parameters.AddWithValue("PersistPlayerId", persistPlayerId);
                command.Parameters.AddWithValue("ArenaGroupIntervalId", arenaGroupIntervalId);
                command.Parameters.AddWithValue("PersistKeyId", persistKey);

                using SQLiteDataReader reader = command.ExecuteReader();

                if (reader.Read())
                {
                    using Stream blobStream = reader.GetStream(0);
                    blobStream.CopyTo(outStream);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error querying the PlayerData table for PersistPlayerId {persistPlayerId}, ArenaGroupIntervalId {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        private static void DbSetPlayerData(SQLiteConnection conn, string playerName, string arenaGroup, PersistInterval interval, int persistKey, MemoryStream dataStream)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(conn, arenaGroup, interval);

            int persistPlayerId = DbGetOrCreatePersistPlayerId(conn, playerName);

            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
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
    ,@Data
)";
                command.Parameters.AddWithValue("PersistPlayerId", persistPlayerId);
                command.Parameters.AddWithValue("ArenaGroupIntervalId", arenaGroupIntervalId);
                command.Parameters.AddWithValue("PersistKeyId", persistKey);
                command.Parameters.AddWithValue("Data", dataStream.ToArray()); // TODO: seems only byte[] is allowed, maybe switch to Microsoft.Data.Sqlite?

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error inserting into the PlayerData table for PersistPlayerId {persistPlayerId}, ArenaGroupIntervalId {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        private static void DbDeletePlayerData(SQLiteConnection conn, string playerName, string arenaGroup, PersistInterval interval, int persistKey)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(conn, arenaGroup, interval);

            int persistPlayerId = DbGetOrCreatePersistPlayerId(conn, playerName);

            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
DELETE FROM PlayerData
WHERE PersistPlayerId = @PersistPlayerId
    AND ArenaGroupIntervalId = @ArenaGroupIntervalId
    AND PersistKeyId = @PersistKeyId";
                command.Parameters.AddWithValue("PersistPlayerId", persistPlayerId);
                command.Parameters.AddWithValue("ArenaGroupIntervalId", arenaGroupIntervalId);
                command.Parameters.AddWithValue("PersistKeyId", persistKey);

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error deleting from PlayerData for PersistPlayerId {persistPlayerId}, ArenaGroupIntervalId {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        private static bool DbGetArenaData(SQLiteConnection conn, string arenaGroup, PersistInterval interval, int persistKey, Stream outStream)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(conn, arenaGroup, interval);

            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
SELECT Data
FROM ArenaData
WHERE ArenaGroupIntervalId = @ArenaGroupIntervalId
    AND PersistKeyId = @PersistKeyId";
                command.Parameters.AddWithValue("ArenaGroupIntervalId", arenaGroupIntervalId);
                command.Parameters.AddWithValue("PersistKeyId", persistKey);

                using SQLiteDataReader reader = command.ExecuteReader();

                if (reader.Read())
                {
                    using var blobStream = reader.GetStream(0);
                    blobStream.CopyTo(outStream);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error querying the ArenaData table for ArenaGroupInterval {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        private static void DbSetArenaData(SQLiteConnection conn, string arenaGroup, PersistInterval interval, int persistKey, MemoryStream dataStream)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(conn, arenaGroup, interval);

            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
INSERT OR REPLACE INTO ArenaData(
     ArenaGroupIntervalId
    ,PersistKeyId
    ,Data
)
VALUES(
     @ArenaGroupIntervalId
    ,@PersistKeyId
    ,@Data
)";
                command.Parameters.AddWithValue("ArenaGroupIntervalId", arenaGroupIntervalId);
                command.Parameters.AddWithValue("PersistKeyId", persistKey);
                command.Parameters.AddWithValue("Data", dataStream.ToArray()); // TODO: seems only byte[] is allowed, maybe switch to Microsoft.Data.Sqlite?

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error inserting into the ArenaData table for ArenaGroupIntervalId {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        private static void DbDeleteArenaData(SQLiteConnection conn, string arenaGroup, PersistInterval interval, int persistKey)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(conn, arenaGroup, interval);

            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
DELETE FROM ArenaData
WHERE ArenaGroupIntervalId = @ArenaGroupIntervalId
    AND PersistKeyId = @PersistKeyId";
                command.Parameters.AddWithValue("ArenaGroupIntervalId", arenaGroupIntervalId);
                command.Parameters.AddWithValue("PersistKeyId", persistKey);

                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error deleting from the ArenaGroup table for ArenaGroupIntervalId {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
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

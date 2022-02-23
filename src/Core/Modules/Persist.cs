using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    public class Persist : IModule, IPersist, IPersistExecutor
    {
        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;

        private InterfaceRegistrationToken _iPersistToken;
        private InterfaceRegistrationToken _iPersistExecutorToken;

        private readonly List<PersistentData<Player>> _playerRegistrations = new();
        private readonly List<PersistentData<Arena>> _arenaRegistrations = new();

        private IPool<PlayerWorkItem> _playerWorkItemPool;
        private IPool<ArenaWorkItem> _arenaWorkItemPool;
        private IPool<IntervalWorkItem> _intervalWorkItemPool;
        private IPool<PutAllWorkItem> _putAllWorkItemPool;

        private readonly BlockingCollection<PersistWorkItem> _workQueue = new();
        private Thread _workerThread;
        private TimeSpan _syncTimeSpan;
        private DateTime? _lastSync;
        private readonly object _lock = new();

        private int _adKey;

        private const string DatabasePath = "./data";
        private const string DatabaseFileName = "SS.Core.Modules.Persist.db";
        private const string ConnectionString = $"Data Source={DatabasePath}/{DatabaseFileName};Version=3;";
        private int _maxRecordLength;

        #region Module memebers

        [ConfigHelp("Persist", "SyncSeconds", ConfigScope.Global, typeof(int), DefaultValue = "180", 
            Description = "The interval at which all persistent data is synced to the database.")]
        [ConfigHelp("Persist", "MaxRecordLength", ConfigScope.Global, typeof(int), DefaultValue = "4096",
            Description = "The maximum # of bytes to store per record.")]
        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloop mainloop,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _broker = broker;
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            if (!InitalizeDatabase())
                return false;

            _playerWorkItemPool = _objectPoolManager.GetPool<PlayerWorkItem>();
            _arenaWorkItemPool = _objectPoolManager.GetPool<ArenaWorkItem>();
            _intervalWorkItemPool = _objectPoolManager.GetPool<IntervalWorkItem>();
            _putAllWorkItemPool = _objectPoolManager.GetPool<PutAllWorkItem>();

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            _syncTimeSpan = TimeSpan.FromSeconds(
                _configManager.GetInt(_configManager.Global, "Persist", "SyncSeconds", 180)).Duration();

            _maxRecordLength = _configManager.GetInt(_configManager.Global, "Persist", "MaxRecordLength", 4096);

            _workerThread = new Thread(PeristWorkerThread);
            _workerThread.Start();

            _iPersistToken = broker.RegisterInterface<IPersist>(this);
            _iPersistExecutorToken = broker.RegisterInterface<IPersistExecutor>(this);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            broker.UnregisterInterface<IPersist>(ref _iPersistToken);
            broker.UnregisterInterface<IPersistExecutor>(ref _iPersistExecutorToken);

            _workQueue.CompleteAdding();
            _workerThread.Join();

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            _arenaManager.FreeArenaData(_adKey);

            return true;
        }

        #endregion

        #region IPersist members

        void IPersist.RegisterPersistentData(PersistentData<Player> registration)
        {
            lock (_lock)
            {
                // TODO: prevent adding a duplicate registration (same key + interval)?
                _playerRegistrations.Add(registration);
            }
        }

        void IPersist.UnregisterPersistentData(PersistentData<Player> registration)
        {
            lock (_lock)
            {
                _playerRegistrations.Remove(registration);
            }
        }

        void IPersist.RegisterPersistentData(PersistentData<Arena> registration)
        {
            lock (_lock)
            {
                // TODO: prevent adding a duplicate registration (same key + interval)?
                _arenaRegistrations.Add(registration);
            }
        }

        void IPersist.UnregisterPersistentData(PersistentData<Arena> registration)
        {
            lock (_lock)
            {
                _arenaRegistrations.Remove(registration);
            }
        }

        #endregion

        #region IPersistExecutor

        void IPersistExecutor.PutPlayer(Player player, Arena arena, Action<Player> callback)
            => QueuePlayerWorkItem(PersistCommand.PutPlayer, player, arena, callback);

        void IPersistExecutor.GetPlayer(Player player, Arena arena, Action<Player> callback)
            => QueuePlayerWorkItem(PersistCommand.GetPlayer, player, arena, callback);

        private void QueuePlayerWorkItem(PersistCommand command, Player player, Arena arena, Action<Player> callback)
        {
            PlayerWorkItem workItem = _playerWorkItemPool.Get();
            workItem.Command = command;
            workItem.Player = player;
            workItem.Arena = arena;
            workItem.Callback = callback;

            _workQueue.Add(workItem);
        }

        void IPersistExecutor.PutArena(Arena arena, Action<Arena> callback) 
            => QueueArenaWorkItem(PersistCommand.PutArena, arena, callback);

        void IPersistExecutor.GetArena(Arena arena, Action<Arena> callback) 
            => QueueArenaWorkItem(PersistCommand.GetArena, arena, callback);

        private void QueueArenaWorkItem(PersistCommand command, Arena arena, Action<Arena> callback)
        {
            ArenaWorkItem workItem = _arenaWorkItemPool.Get();
            workItem.Command = command;
            workItem.Arena = arena;
            workItem.Callback = callback;

            _workQueue.Add(workItem);
        }

        void IPersistExecutor.EndInterval(PersistInterval interval, string arenaGroupOrArenaName)
        {
            if (string.IsNullOrWhiteSpace(arenaGroupOrArenaName))
                arenaGroupOrArenaName = Constants.ArenaGroup_Global;

            QueueEndIntervalWorkItem(interval, arenaGroupOrArenaName);
        }

        void IPersistExecutor.EndInterval(PersistInterval interval, Arena arena)
        {
            QueueEndIntervalWorkItem(interval, GetArenaGroup(arena, interval));
        }

        private void QueueEndIntervalWorkItem(PersistInterval interval, string arenaGroup)
        {
            IntervalWorkItem workItem = _intervalWorkItemPool.Get();
            workItem.Command = PersistCommand.EndInterval;
            workItem.Interval = interval;
            workItem.ArenaGroup = arenaGroup;

            _workQueue.Add(workItem);
        }

        void IPersistExecutor.SaveAll(Action completed)
        {
            QueuePutAllWorkItem(completed);
        }

        private void QueuePutAllWorkItem(Action callback)
        {
            lock (_lock)
            {
                _lastSync = DateTime.UtcNow;
            }

            PutAllWorkItem workItem = _putAllWorkItemPool.Get();
            workItem.Command = PersistCommand.PutAll;
            workItem.Callback = callback;

            _workQueue.Add(workItem);
        }

        #endregion

        [ConfigHelp("General", "ScoreGroup", ConfigScope.Arena, typeof(string),
            Description = 
            "If this is set, it will be used as the score identifier for" +
            "shared scores for this arena(unshared scores, e.g. per - game" +
            "scores, always use the arena name as the identifier).Setting" +
            "this to the same value in several different arenas will cause" +
            "them to share scores. ")]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (arena[_adKey] is not ArenaData ad)
                return;

            if (action == ArenaAction.Create)
            {
                string arenaGroup = _configManager.GetStr(arena.Cfg, "General", "ScoreGroup");
                if (!string.IsNullOrWhiteSpace(arenaGroup))
                    ad.ArenaGroup = arenaGroup;
                else
                    ad.ArenaGroup = arena.BaseName;
            }
        }

        private void PeristWorkerThread()
        {
            while (!_workQueue.IsCompleted)
            {
                TimeSpan waitTimeSpan;

                lock (_lock)
                {
                    if (_lastSync == null)
                    {
                        waitTimeSpan = _syncTimeSpan;
                    }
                    else
                    {
                        waitTimeSpan = DateTime.UtcNow - (_lastSync.Value + _syncTimeSpan);
                        if (waitTimeSpan < TimeSpan.Zero)
                            waitTimeSpan = TimeSpan.Zero;
                    }
                }

                if (!_workQueue.TryTake(out PersistWorkItem workItem, waitTimeSpan))
                {
                    // Did not get a workitem. This means either:
                    // we've either been signaled to shut down
                    // OR
                    // it's time to do a full sync (put all data to the database)
                    if (!_workQueue.IsCompleted)
                    {
                        // Not signaled to shut down, which means we should do a full sync.
                        // The sync is done periodically so that if there were a server crash or power outage,
                        // we'd at least have the data that was saved since the last sync was committed.
                        DoPutAll();
                    }

                    continue;
                }

                PlayerWorkItem playerWorkItem;
                ArenaWorkItem arenaWorkItem;

                switch (workItem.Command)
                {
                    case PersistCommand.Null:
                        break;

                    case PersistCommand.GetPlayer:
                        playerWorkItem = workItem as PlayerWorkItem;
                        if (playerWorkItem != null)
                        {
                            lock (_lock)
                            {
                                foreach (PersistentData<Player> registration in _playerRegistrations)
                                {
                                    GetOnePlayer(registration, playerWorkItem.Player, playerWorkItem.Arena);
                                }
                            }
                        }
                        break;

                    case PersistCommand.PutPlayer:
                        playerWorkItem = workItem as PlayerWorkItem;
                        if (playerWorkItem != null)
                        {
                            DoPutPlayer(playerWorkItem.Player, playerWorkItem.Arena);
                        }
                        break;

                    case PersistCommand.GetArena:
                        arenaWorkItem = workItem as ArenaWorkItem;
                        if (arenaWorkItem != null)
                        {
                            lock (_lock)
                            {
                                foreach(PersistentData<Arena> registration in _arenaRegistrations)
                                {
                                    GetOneArena(registration, arenaWorkItem.Arena);
                                }
                            }
                        }
                        break;

                    case PersistCommand.PutArena:
                        arenaWorkItem = workItem as ArenaWorkItem;
                        if (arenaWorkItem != null)
                        {
                            lock (_lock)
                            {
                                foreach (PersistentData<Arena> registration in _arenaRegistrations)
                                {
                                    PutOneArena(registration, arenaWorkItem.Arena);
                                }
                            }
                        }
                        break;

                    case PersistCommand.PutAll:
                        lock (_lock)
                        {
                            DoPutAll();
                        }
                        break;

                    case PersistCommand.EndInterval:
                        IntervalWorkItem intervalWorkItem = workItem as IntervalWorkItem;
                        if (intervalWorkItem != null)
                        {
                            lock (_lock)
                            {
                                DoEndInterval(intervalWorkItem.Interval, intervalWorkItem.ArenaGroup);
                            }
                        }
                        break;

                    //case PersistCommand.GetGeneric:
                    //    break;

                    //case PersistCommand.PutGeneric:
                    //    break;

                    default:
                        break;
                }

                _mainloop.QueueMainWorkItem(MainloopWorkItem_ExecuteCallbacks, workItem);
            }

            void DoEndInterval(PersistInterval interval, string arenaGroup)
            {
                if (interval == PersistInterval.Forever || interval == PersistInterval.ForeverNotShared)
                    return; // forever can't be ended

                bool isGlobal = string.Equals(arenaGroup, Constants.ArenaGroup_Global);

                PlayerState minStatus, maxStatus;

                if (isGlobal)
                {
                    // global data is loaded during S_WAIT_GLOBAL_SYNC,
                    // so we want to perform the getting/clearing if the player is after that.
                    minStatus = PlayerState.DoGlobalCallbacks;

                    // after we've saved global data for the last time, status goes to S_TIMEWAIT,
                    // so if we're before that, we still have data to save.
                    maxStatus = PlayerState.WaitGlobalSync2;
                }
                else
                {
                    // similar to above, but for arena data
                    minStatus = PlayerState.ArenaRespAndCBS;
                    maxStatus = PlayerState.WaitArenaSync2;
                }

                //
                // players
                //

                _playerData.Lock();

                try
                {
                    foreach (Player p in _playerData.PlayerList)
                    {
                        Arena arena = p.Arena;

                        if (p.Status >= minStatus
                            && p.Status <= maxStatus
                            && (isGlobal
                                || (arena != null && string.Equals(arenaGroup, GetArenaGroup(arena, interval), StringComparison.OrdinalIgnoreCase))))
                        {
                            foreach (PersistentData<Player> registration in _playerRegistrations)
                            {
                                if (registration.Interval == interval)
                                {
                                    if (registration.Scope == PersistScope.Global && isGlobal)
                                    {
                                        PutOnePlayer(registration, p, null);
                                        registration.ClearData(p);
                                    }
                                    else if (registration.Scope == PersistScope.PerArena && !isGlobal)
                                    {
                                        PutOnePlayer(registration, p, arena);
                                        registration.ClearData(p);
                                    }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                //
                // arenas
                //

                if (isGlobal)
                {
                    foreach (PersistentData<Arena> registration in _arenaRegistrations)
                    {
                        if (registration.Interval == interval
                            && registration.Scope == PersistScope.Global)
                        {
                            PutOneArena(registration, null);
                            registration.ClearData(null);
                        }
                    }
                }
                else
                {
                    _arenaManager.Lock();

                    try
                    {
                        foreach (Arena arena in _arenaManager.ArenaList)
                        {
                            if (string.Equals(GetArenaGroup(arena, interval), arenaGroup, StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (PersistentData<Arena> registration in _arenaRegistrations)
                                {
                                    if (registration.Interval == interval
                                        && registration.Scope == PersistScope.PerArena)
                                    {
                                        PutOneArena(registration, arena);
                                        registration.ClearData(null);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        _arenaManager.Unlock();
                    }
                }

                //
                // Update the ArenaGroupInterval
                //

                try
                {
                    using SQLiteConnection conn = new(ConnectionString);
                    conn.Open();

                    using (SQLiteTransaction transaction = conn.BeginTransaction())
                    {
                        DbCreateArenaGroupIntervalAndSetCurrent(conn, arenaGroup, interval);

                        transaction.Commit();
                    }
                }
                catch (Exception ex)
                {
                    LogException(
                        $"Error calling the database to create a new ArenaGroupInterval and set it as current for ArenaGroup {arenaGroup}, Interval {interval}.",
                        ex);
                    return;
                }
            }

            void DoPutPlayer(Player player, Arena arena)
            {
                if (player == null)
                    return;

                lock (_lock)
                {
                    foreach (PersistentData<Player> registration in _playerRegistrations)
                    {
                        PutOnePlayer(registration, player, arena);
                    }
                }
            }

            void DoPutArena(Arena arena)
            {
                lock (_lock)
                {
                    foreach (PersistentData<Arena> registration in _arenaRegistrations)
                    {
                        PutOneArena(registration, arena);
                    }
                }
            }

            void DoPutAll()
            {
                // sync all players
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.PlayerList)
                    {
                        if (player.Status == PlayerState.Playing)
                        {
                            DoPutPlayer(player, null); // global player data
                            if (player.Arena != null)
                            {
                                DoPutPlayer(player, player.Arena); // per-arena player data
                            }
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                // sync all arenas
                _arenaManager.Lock();

                try
                {
                    foreach (Arena arena in _arenaManager.ArenaList)
                    {
                        if (arena.Status == ArenaState.Running)
                        {
                            lock (_lock)
                            {
                                DoPutArena(arena); // per-arena arena data
                            }
                        }
                    }
                }
                finally
                {
                    _arenaManager.Unlock();
                }

                // global arena data
                DoPutArena(null);
            }
        }

        private void PutOneArena(PersistentData<Arena> registration, Arena arena)
        {
            if (registration == null)
                return;

            // Check correct scope.
            if (registration.Scope == PersistScope.Global && arena != null
                || registration.Scope == PersistScope.PerArena && arena == null)
            {
                return;
            }

            string arenaGroup = GetArenaGroup(arena, registration.Interval);

            using (MemoryStream dataStream = new(_maxRecordLength)) // TODO: reconsider this, maybe use ArrayPool and pass it as a Span instead?
            {
                registration.GetData(arena, dataStream);

                if (dataStream.Length > 0)
                {
                    dataStream.Position = 0;

                    try
                    {
                        using SQLiteConnection conn = new(ConnectionString);
                        conn.Open();

                        using (SQLiteTransaction transaction = conn.BeginTransaction())
                        {
                            DbSetArenaData(conn, arenaGroup, registration.Interval, registration.Key, dataStream);

                            transaction.Commit();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException(
                            arena,
                            $"Error saving arena data to the database for ArenaGroup {arenaGroup}, Interval {registration.Interval}, Key {registration.Key}.",
                            ex);
                        return;
                    }
                }
                else
                {
                    try
                    {
                        using SQLiteConnection conn = new(ConnectionString);
                        conn.Open();

                        using (SQLiteTransaction transaction = conn.BeginTransaction())
                        {
                            DbDeleteArenaData(conn, arenaGroup, registration.Interval, registration.Key);

                            transaction.Commit();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException(
                            arena,
                            $"Error deleting arena data from the database for ArenaGroup {arenaGroup}, Interval {registration.Interval}, Key {registration.Key}.",
                            ex);
                        return;
                    }
                }
            }
        }

        private void DbDeleteArenaData(SQLiteConnection conn, string arenaGroup, PersistInterval interval, int persistKey)
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

        private void DbSetArenaData(SQLiteConnection conn, string arenaGroup, PersistInterval interval, int persistKey, MemoryStream dataStream)
        {
            int arenaGroupIntervalId = DbGetOrCreateCurrentArenaGroupIntervalId(conn, arenaGroup, interval);

            try
            {
                using SQLiteCommand command = conn.CreateCommand();
                command.CommandText = @"
INSERT INTO ArenaData(
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

        private void GetOneArena(PersistentData<Arena> registration, Arena arena)
        {
            if (registration == null)
                return;

            // Check correct scope.
            if(registration.Scope == PersistScope.Global && arena != null
                || registration.Scope == PersistScope.PerArena && arena == null)
            {
                return;
            }

            registration.ClearData(arena);

            string arenaGroup = GetArenaGroup(arena, registration.Interval);

            using (MemoryStream dataStream = new(_maxRecordLength)) // TODO: reconsider this, maybe use ArrayPool and pass it as a Span instead?
            {
                try
                {
                    using SQLiteConnection conn = new(ConnectionString);
                    conn.Open();

                    using (SQLiteTransaction transaction = conn.BeginTransaction())
                    {
                        DbGetArenaData(conn, arenaGroup, registration.Interval, registration.Key, dataStream);

                        transaction.Commit();
                    }
                }
                catch (Exception ex)
                {
                    LogException(
                        arena, 
                        $"Error getting arena data from the database for ArenaGroup {arenaGroup}, Interval {registration.Interval}, Key {registration.Key}).",
                        ex);
                    return;
                }

                dataStream.Position = 0;
                registration.SetData(arena, dataStream);
            }
        }

        private void DbGetArenaData(SQLiteConnection conn, string arenaGroup, PersistInterval interval, int persistKey, MemoryStream outStream)
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
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error querying the ArenaData table for ArenaGroupInterval {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        private void MainloopWorkItem_ExecuteCallbacks(PersistWorkItem workItem)
        {
            if (workItem == null)
                return;

            try
            {
                workItem.ExecuteCallback();

                if (workItem.Command == PersistCommand.EndInterval
                    && workItem is IntervalWorkItem intervalWorkItem)
                {
                    PersistIntervalEndedCallback.Fire(_broker, intervalWorkItem.Interval);
                }
            }
            finally
            {
                // return the workItem to its pool
                workItem.Dispose();
            }
        }

        private void PutOnePlayer(PersistentData<Player> registration, Player player, Arena arena)
        {
            if (registration == null)
                return;

            if (player == null)
                return;

            // Check correct scope.
            if ((registration.Scope == PersistScope.Global && arena != null)
                || (registration.Scope == PersistScope.PerArena && arena == null))
            {
                return;
            }

            string arenaGroup = GetArenaGroup(arena, registration.Interval);

            using (MemoryStream dataStream = new(_maxRecordLength)) // TODO: reconsider this, maybe use ArrayPool and pass it as a Span instead?
            {
                registration.GetData(player, dataStream);

                if (dataStream.Length > 0)
                {
                    dataStream.Position = 0;

                    try
                    {
                        using SQLiteConnection conn = new(ConnectionString);
                        conn.Open();

                        using (SQLiteTransaction transaction = conn.BeginTransaction())
                        {
                            DbSetPlayerData(conn, player.Name, arenaGroup, registration.Interval, registration.Key, dataStream);

                            transaction.Commit();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException(
                            player,
                            $"Error saving player data to the database for ArenaGroup {arenaGroup}, Interval {registration.Interval}, Key {registration.Key}).",
                            ex);
                        return;
                    }
                }
                else
                {
                    try
                    {
                        using SQLiteConnection conn = new(ConnectionString);
                        conn.Open();

                        using (SQLiteTransaction transaction = conn.BeginTransaction())
                        {
                            DbDeletePlayerData(conn, player.Name, arenaGroup, registration.Interval, registration.Key);

                            transaction.Commit();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogException(
                            player, 
                            $"Error deleting player data from the database for ArenaGroup {arenaGroup}, Interval {registration.Interval}, Key {registration.Key}).",
                            ex);
                        return;
                    }
                }
            }
        }

        private void DbDeletePlayerData(SQLiteConnection conn, string playerName, string arenaGroup, PersistInterval interval, int persistKey)
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

        private void DbSetPlayerData(SQLiteConnection conn, string playerName, string arenaGroup, PersistInterval interval, int persistKey, MemoryStream dataStream)
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

        private void GetOnePlayer(PersistentData<Player> registration, Player player, Arena arena)
        {
            if (registration == null)
                return;

            if (player == null)
                return;

            // Check correct scope.
            if (registration.Scope == PersistScope.Global && arena != null
                || registration.Scope == PersistScope.PerArena && arena == null)
            {
                return;
            }

            registration.ClearData(player);

            string arenaGroup = GetArenaGroup(arena, registration.Interval);

            using (MemoryStream dataStream = new(_maxRecordLength)) // TODO: reconsider this, maybe use ArrayPool and pass it as a Span instead?
            {
                try
                {
                    using SQLiteConnection conn = new(ConnectionString);
                    conn.Open();

                    using (SQLiteTransaction transaction = conn.BeginTransaction())
                    {
                        DbGetPlayerData(conn, player.Name, arenaGroup, registration.Interval, registration.Key, dataStream);

                        transaction.Commit();
                    }
                }
                catch (Exception ex)
                {
                    LogException(
                        player,
                        $"Error getting player data from the database for ArenaGroup {arenaGroup}, Interval {registration.Interval}, Key {registration.Key}).",
                        ex);
                    return;
                }

                dataStream.Position = 0;
                registration.SetData(player, dataStream);
            }
        }

        private string GetArenaGroup(Arena arena, PersistInterval interval)
        {
            if (arena == null)
                return Constants.ArenaGroup_Global;

            if (interval.IsShared()
                && arena[_adKey] is ArenaData ad)
            {
                return ad.ArenaGroup;
            }
            else
            {
                return arena.Name;
            }
        }

        private void DbGetPlayerData(SQLiteConnection conn, string playerName, string arenaGroup, PersistInterval interval, int persistKey, Stream outStream)
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

                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        using (Stream blobStream = reader.GetStream(0))
                        {
                            blobStream.CopyTo(outStream);
                        }

                        //ReadBlobToStream(reader, outStream);

                        //SQLiteBlob blob = reader.GetBlob(0, true);
                        //blob.Read()
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error querying the PlayerData table for PersistPlayerId {persistPlayerId}, ArenaGroupIntervalId {arenaGroupIntervalId}, PersistKeyId {persistKey}.", ex);
            }
        }

        //private static void ReadBlobToStream(SQLiteDataReader reader, Stream stream)
        //{
        //    byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);

        //    try
        //    {
        //        long fieldOffset = 0;
        //        long bytesRead;
        //        while ((bytesRead = reader.GetBytes(0, fieldOffset, buffer, 0, buffer.Length)) > 0)
        //        {
        //            stream.Write(buffer, 0, (int)bytesRead);
        //            fieldOffset += bytesRead;
        //        }
        //    }
        //    finally
        //    {
        //        ArrayPool<byte>.Shared.Return(buffer, true);
        //    }
        //}

        private int DbGetOrCreatePersistPlayerId(SQLiteConnection conn, string playerName)
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

        private int DbGetOrCreateCurrentArenaGroupIntervalId(SQLiteConnection conn, string arenaGroup, PersistInterval interval)
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

        private int DbCreateArenaGroupIntervalAndSetCurrent(SQLiteConnection conn, string arenaGroup, PersistInterval interval)
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

        private bool InitalizeDatabase()
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

        public class ArenaData
        {
            /// <summary>
            /// For shared intervals.
            /// </summary>
            public string ArenaGroup { get; set; }
        }

        private enum PersistCommand
        {
            Null,
            GetPlayer,
            PutPlayer,
            GetArena,
            PutArena,
            PutAll,
            EndInterval,
            //GetGeneric,
            //PutGeneric,
        }

        private abstract class PersistWorkItem : PooledObject
        {
            protected PersistCommand _command;
            public abstract PersistCommand Command { get; set;  }

            public virtual void ExecuteCallback()
            {
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                if (isDisposing)
                {
                    _command = PersistCommand.Null;
                }
            }
        }

        private class ArenaWorkItem : PersistWorkItem
        {
            public override PersistCommand Command
            {
                get { return _command; }
                set
                {
                    if (value != PersistCommand.GetArena && value != PersistCommand.PutArena)
                        throw new ArgumentOutOfRangeException(nameof(value));

                    _command = value;
                }
            }

            public Arena Arena { get; set; }
            public Action<Arena> Callback { get; set; }

            public override void ExecuteCallback()
            {
                Callback?.Invoke(Arena);
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                if (isDisposing)
                {                    
                    Arena = null;
                    Callback = null;
                }
            }
        }

        private class PlayerWorkItem : PersistWorkItem
        {
            public override PersistCommand Command
            {
                get { return _command; }
                set
                {
                    if (value != PersistCommand.GetPlayer && value != PersistCommand.PutPlayer)
                        throw new ArgumentOutOfRangeException(nameof(value));

                    _command = value;
                }
            }

            public Player Player { get; set; }
            public Arena Arena { get; set; }
            public Action<Player> Callback { get; set; }

            public override void ExecuteCallback()
            {
                Callback?.Invoke(Player);
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                if (isDisposing)
                {
                    Player = null;
                    Arena = null;
                    Callback = null;
                }
            }
        }

        private class PutAllWorkItem : PersistWorkItem
        {
            public override PersistCommand Command
            {
                get { return _command; }
                set
                {
                    if (value != PersistCommand.PutAll)
                        throw new ArgumentOutOfRangeException(nameof(value));

                    _command = value;
                }
            }

            public Action Callback { get; set; }

            public override void ExecuteCallback()
            {
                Callback?.Invoke();
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                if(isDisposing)
                {
                    Callback = null;
                }
            }
        }

        private class IntervalWorkItem : PersistWorkItem
        {
            public override PersistCommand Command
            {
                get { return _command; }
                set
                {
                    if (value != PersistCommand.EndInterval)
                        throw new ArgumentOutOfRangeException(nameof(value));

                    _command = value;
                }
            }

            public PersistInterval Interval { get; set; }
            public string ArenaGroup { get; set; }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                if (isDisposing)
                {
                    ArenaGroup = null;
                    Interval = default;
                }
            }
        }
    }
}

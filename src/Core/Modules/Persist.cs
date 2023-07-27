using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality to persist information to a database.
    /// </summary>
    [CoreModuleInfo]
    public sealed class Persist : IModule, IPersist, IPersistExecutor, IDisposable
    {
        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private IConfigManager _configManager;
        private IMainloop _mainloop;
        private IObjectPoolManager _objectPoolManager;
        private IPersistDatastore _persistDatastore;
        private IPlayerData _playerData;

        private InterfaceRegistrationToken<IPersist> _iPersistToken;
        private InterfaceRegistrationToken<IPersistExecutor> _iPersistExecutorToken;

        private readonly List<PersistentData<Player>> _playerRegistrations = new();
        private readonly List<PersistentData<Arena>> _arenaRegistrations = new();

        private Pool<PlayerWorkItem> _playerWorkItemPool;
        private Pool<ArenaWorkItem> _arenaWorkItemPool;
        private Pool<IntervalWorkItem> _intervalWorkItemPool;
        private Pool<ResetGameIntervalWorkItem> _resetGameIntervalWorkItemPool;
        private Pool<PutAllWorkItem> _putAllWorkItemPool;
        private ObjectPool<MemoryStream> _memoryStreamPool = ObjectPool.Create(new MemoryStreamPooledObjectPolicy()); // Note: This creates a DisposableObjectPool.

        private readonly BlockingCollection<PersistWorkItem> _workQueue = new();
        private Thread _workerThread;
        private TimeSpan _syncTimeSpan;
        private DateTime? _nextSync;
        private readonly object _lock = new();

        private ArenaDataKey<ArenaData> _adKey;

        private static int _maxRecordLength;

        #region Module memebers

        [ConfigHelp("Persist", "SyncSeconds", ConfigScope.Global, typeof(int), DefaultValue = "180",
            Description = "The interval at which all persistent data is synced to the database.")]
        [ConfigHelp("Persist", "MaxRecordLength", ConfigScope.Global, typeof(int), DefaultValue = "4096",
            Description = "The maximum # of bytes to store per record.")]
        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IConfigManager configManager,
            IMainloop mainloop,
            IObjectPoolManager objectPoolManager,
            IPersistDatastore persistDatastore,
            IPlayerData playerData)
        {
            _broker = broker;
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _persistDatastore = persistDatastore ?? throw new ArgumentNullException(nameof(persistDatastore));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            if (!_persistDatastore.Open())
                return false;

            _playerWorkItemPool = _objectPoolManager.GetPool<PlayerWorkItem>();
            _arenaWorkItemPool = _objectPoolManager.GetPool<ArenaWorkItem>();
            _intervalWorkItemPool = _objectPoolManager.GetPool<IntervalWorkItem>();
            _resetGameIntervalWorkItemPool = _objectPoolManager.GetPool<ResetGameIntervalWorkItem>();
            _putAllWorkItemPool = _objectPoolManager.GetPool<PutAllWorkItem>();

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            _syncTimeSpan = TimeSpan.FromSeconds(
                _configManager.GetInt(_configManager.Global, "Persist", "SyncSeconds", 180)).Duration();

            if (_syncTimeSpan < TimeSpan.FromSeconds(10))
                _syncTimeSpan = TimeSpan.FromSeconds(10);

            _maxRecordLength = _configManager.GetInt(_configManager.Global, "Persist", "MaxRecordLength", 4096);

            _workerThread = new Thread(PeristWorkerThread);
            _workerThread.Name = nameof(Persist);
            _workerThread.Start();

            _iPersistToken = broker.RegisterInterface<IPersist>(this);
            _iPersistExecutorToken = broker.RegisterInterface<IPersistExecutor>(this);

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iPersistToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iPersistExecutorToken) != 0)
                return false;

            _workQueue.CompleteAdding();
            _workerThread.Join();

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            _arenaManager.FreeArenaData(ref _adKey);

            _persistDatastore.Close();

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
        {
            if (!player.Flags.Authenticated)
            {
                callback?.Invoke(player);
                return;
            }

            QueuePlayerWorkItem(PersistCommand.PutPlayer, player, arena, callback);
        }

        void IPersistExecutor.GetPlayer(Player player, Arena arena, Action<Player> callback)
        {
            if (!player.Flags.Authenticated)
            {
                callback?.Invoke(player);
                return;
            }

            QueuePlayerWorkItem(PersistCommand.GetPlayer, player, arena, callback);
        }

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

            QueueIntervalWorkItem(PersistCommand.EndInterval, interval, arenaGroupOrArenaName);
        }

        void IPersistExecutor.EndInterval(PersistInterval interval, Arena arena)
        {
            QueueIntervalWorkItem(PersistCommand.EndInterval, interval, GetArenaGroup(arena, interval));
        }

        private void QueueIntervalWorkItem(PersistCommand command, PersistInterval interval, string arenaGroup)
        {
            IntervalWorkItem workItem = _intervalWorkItemPool.Get();
            workItem.Command = command;
            workItem.Interval = interval;
            workItem.ArenaGroup = arenaGroup;

            _workQueue.Add(workItem);
        }

        void IPersistExecutor.ResetGameInterval(Arena arena, Action<Arena> callback)
        {
            if (arena == null)
                throw new ArgumentNullException(nameof(arena));

            ResetGameIntervalWorkItem workItem = _resetGameIntervalWorkItemPool.Get();
            workItem.Command = PersistCommand.ResetGameInterval;
            workItem.Arena = arena;
            workItem.Callback = callback;

            _workQueue.Add(workItem);
        }

        void IPersistExecutor.SaveAll(Action completed)
        {
            PutAllWorkItem workItem = _putAllWorkItemPool.Get();
            workItem.Command = PersistCommand.PutAll;
            workItem.Callback = completed;

            _workQueue.Add(workItem);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_memoryStreamPool is IDisposable disposable)
            {
                disposable.Dispose();
                _memoryStreamPool = null;
            }
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
            if (!arena.TryGetExtraData(_adKey, out ArenaData ad))
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
            while (true)
            {
                TimeSpan waitTimeSpan;

                lock (_lock)
                {
                    _nextSync ??= DateTime.UtcNow + _syncTimeSpan;

                    waitTimeSpan = _nextSync.Value - DateTime.UtcNow;
                    if (waitTimeSpan < TimeSpan.Zero)
                        waitTimeSpan = TimeSpan.Zero;
                }

                if (!_workQueue.TryTake(out PersistWorkItem workItem, waitTimeSpan))
                {
                    // Did not get a workitem. This means either:
                    // we've either been signaled to shut down (no more items will be added)
                    // OR
                    // it's time to do a full sync (put all data to the database)
                    if (_workQueue.IsCompleted)
                    {
                        return;
                    }
                    else
                    {
                        // Not signaled to shut down, which means we should do a full sync.
                        // The sync is done periodically so that if there were a server crash or power outage,
                        // we'd at least have the data that was saved since the last sync was committed.
                        lock (_lock)
                        {
                            DoPutAll();
                        }
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
                                foreach (PersistentData<Arena> registration in _arenaRegistrations)
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
                        if (workItem is IntervalWorkItem intervalWorkItem)
                        {
                            lock (_lock)
                            {
                                DoEndInterval(intervalWorkItem.Interval, intervalWorkItem.ArenaGroup);
                            }
                        }
                        break;

                    case PersistCommand.ResetGameInterval:
                        if (workItem is ResetGameIntervalWorkItem resetIntervalWorkItem)
                        {
                            lock (_lock)
                            {
                                DoResetGameInterval(resetIntervalWorkItem.Arena);
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

                if (!_mainloop.QueueMainWorkItem(MainloopWorkItem_ExecuteCallbacksAndDispose, workItem)
                    && workItem.Command == PersistCommand.PutAll)
                {
                    // Couldn't queue a mainloop workitem. This will happen when the server is shutting down.
                    // When the mainloop exits, that thread requests that we save everything by adding a PutAll request, and it waits for us to execute the callback.
                    // Do the callback on worker thread.
                    MainloopWorkItem_ExecuteCallbacksAndDispose(workItem);
                }
            }

            void DoResetGameInterval(Arena arena)
            {
                if (arena == null)
                    return;

                PlayerState minStatus = PlayerState.ArenaRespAndCBS;
                PlayerState maxStatus = PlayerState.WaitArenaSync2;

                //
                // Players
                //

                _playerData.Lock();

                try
                {
                    foreach (Player p in _playerData.Players)
                    {
                        Arena playerArena = p.Arena;

                        if (p.Status >= minStatus
                            && p.Status <= maxStatus
                            && p.Arena == arena)
                        {
                            foreach (PersistentData<Player> registration in _playerRegistrations)
                            {
                                if (registration.Interval == PersistInterval.Game
                                    && registration.Scope == PersistScope.PerArena)
                                {
                                    registration.ClearData(p);
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
                // Arenas
                //

                foreach (PersistentData<Arena> registration in _arenaRegistrations)
                {
                    if (registration.Interval == PersistInterval.Game
                        && registration.Scope == PersistScope.PerArena)
                    {
                        registration.ClearData(arena);
                    }
                }

                //
                // Remove from the database.
                //

                _persistDatastore.ResetGameInterval(arena.Name);
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
                    foreach (Player p in _playerData.Players)
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
                        foreach (Arena arena in _arenaManager.Arenas)
                        {
                            if (string.Equals(GetArenaGroup(arena, interval), arenaGroup, StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (PersistentData<Arena> registration in _arenaRegistrations)
                                {
                                    if (registration.Interval == interval
                                        && registration.Scope == PersistScope.PerArena)
                                    {
                                        PutOneArena(registration, arena);
                                        registration.ClearData(arena);
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

                _persistDatastore.CreateArenaGroupIntervalAndMakeCurrent(arenaGroup, interval);
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
                    foreach (Player player in _playerData.Players)
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
                    foreach (Arena arena in _arenaManager.Arenas)
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

                _nextSync = DateTime.UtcNow + _syncTimeSpan;
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

            MemoryStream dataStream = _memoryStreamPool.Get();

            try
            {
                registration.GetData(arena, dataStream);

                if (dataStream.Length > 0)
                {
                    dataStream.Position = 0;
                    _persistDatastore.SetArenaData(arenaGroup, registration.Interval, registration.Key, dataStream);
                }
                else
                {
                    _persistDatastore.DeleteArenaData(arenaGroup, registration.Interval, registration.Key);
                }
            }
            finally
            {
                _memoryStreamPool.Return(dataStream);
            }
        }

        private void GetOneArena(PersistentData<Arena> registration, Arena arena)
        {
            if (registration == null)
                return;

            // Check correct scope.
            if (registration.Scope == PersistScope.Global && arena != null
                || registration.Scope == PersistScope.PerArena && arena == null)
            {
                return;
            }

            registration.ClearData(arena);

            string arenaGroup = GetArenaGroup(arena, registration.Interval);

            MemoryStream dataStream = _memoryStreamPool.Get();

            try
            {
                if (_persistDatastore.GetArenaData(arenaGroup, registration.Interval, registration.Key, dataStream))
                {
                    dataStream.Position = 0;
                    registration.SetData(arena, dataStream);
                }
            }
            finally
            {
                _memoryStreamPool.Return(dataStream);
            }
        }

        private void MainloopWorkItem_ExecuteCallbacksAndDispose(PersistWorkItem workItem)
        {
            if (workItem == null)
                return;

            try
            {
                workItem.ExecuteCallback();

                if (workItem.Command == PersistCommand.EndInterval
                    && workItem is IntervalWorkItem intervalWorkItem)
                {
                    PersistIntervalEndedCallback.Fire(_broker, intervalWorkItem.Interval, intervalWorkItem.ArenaGroup);
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

            MemoryStream dataStream = _memoryStreamPool.Get();

            try
            {
                registration.GetData(player, dataStream);

                if (dataStream.Length > 0)
                {
                    dataStream.Position = 0;
                    _persistDatastore.SetPlayerData(player, arenaGroup, registration.Interval, registration.Key, dataStream);
                }
                else
                {
                    _persistDatastore.DeletePlayerData(player, arenaGroup, registration.Interval, registration.Key);
                }
            }
            finally
            {
                _memoryStreamPool.Return(dataStream);
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

            MemoryStream dataStream = _memoryStreamPool.Get();

            try
            {
                if (_persistDatastore.GetPlayerData(player, arenaGroup, registration.Interval, registration.Key, dataStream))
                {
                    dataStream.Position = 0;
                    registration.SetData(player, dataStream);
                }
            }
            finally
            {
                _memoryStreamPool.Return(dataStream);
            }
        }

        private string GetArenaGroup(Arena arena, PersistInterval interval)
        {
            if (arena == null)
                return Constants.ArenaGroup_Global;

            if (interval.IsShared()
                && arena.TryGetExtraData(_adKey, out ArenaData ad))
            {
                return ad.ArenaGroup;
            }
            else
            {
                return arena.Name;
            }
        }

        public class ArenaData : IPooledExtraData
        {
            /// <summary>
            /// For shared intervals.
            /// </summary>
            public string ArenaGroup { get; set; }

            public void Reset()
            {
                ArenaGroup = null;
            }
        }

        /// <summary>
        /// Types of commands that the worker thread can be given.
        /// </summary>
        private enum PersistCommand
        {
            Null,
            GetPlayer,
            PutPlayer,
            GetArena,
            PutArena,
            PutAll,
            EndInterval,
            ResetGameInterval,
            //GetGeneric,
            //PutGeneric,
        }

        #region WorkItem helper classes

        private abstract class PersistWorkItem : PooledObject
        {
            protected PersistCommand _command;
            public abstract PersistCommand Command { get; set; }

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

                if (isDisposing)
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

        private class ResetGameIntervalWorkItem : PersistWorkItem
        {
            public override PersistCommand Command
            {
                get { return _command; }
                set
                {
                    if (value != PersistCommand.ResetGameInterval)
                        throw new ArgumentOutOfRangeException(nameof(value));

                    _command = value;
                }
            }

            public Arena Arena { get; set; }

            public Action<Arena> Callback { get; set; }

            public override void ExecuteCallback()
            {
                Callback(Arena);
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

        #endregion

        private class MemoryStreamPooledObjectPolicy : IPooledObjectPolicy<MemoryStream>
        {
            public MemoryStream Create()
            {
                return new MemoryStream(_maxRecordLength);
            }

            public bool Return(MemoryStream obj)
            {
                if (obj is null)
                    return false;

                obj.Position = 0;
                obj.SetLength(0);
                return true;
            }
        }
    }
}

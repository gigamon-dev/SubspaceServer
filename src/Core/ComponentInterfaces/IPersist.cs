using System;
using System.IO;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Known built-in keys for persisting data.
    /// </summary>
    /// <remarks>
    /// The values of these keys match the ones used in ASSS. However, this server doesn't serialize data in the same binary format as ASSS. So it's not compatible.
    /// </remarks>
    public enum PersistKey
    {
        Stats = 1,
        StatsEndingTime = 2,
        Jackpot = 12,
        StaticFlags = 20,
        GameShipLock = 46,
        Chat = 47,
    }

    /// <summary>
    /// Represents the interval for persistent data.
    /// </summary>
    public enum PersistInterval
    {
        /// <summary>
        /// Data stored forever.
        /// </summary>
        Forever = 0,

        /// <summary>
        /// For the a single reset.
        /// </summary>
        Reset,

        /// <summary>
        /// For the duration of a map rotation.
        /// </summary>
        MapRotation,

        /// <summary>
        /// For a single game within a reset.
        /// </summary>
        Game = 5,

        /// <summary>
        /// Data stored forever, but not shared between arenas.
        /// </summary>
        ForeverNotShared,
    }

    public static class PersistIntervalExtensions
    {
        /// <summary>
        /// Gets whether <see cref="PersistScope.PerArena"/> data for a <see cref="PersistInterval"/> is shared between arenas with the same base arena name.
        /// </summary>
        /// <remarks>
        /// For example, whether the data is shared amongst arenas: "foo", "foo1", "foo2", ...
        /// Similarly, for the public arenas "0", "1", ...
        /// </remarks>
        /// <param name="interval"></param>
        /// <returns></returns>
        public static bool IsShared(this PersistInterval interval)
        {
            return (int)interval < 5;
        }
    }

    /// <summary>
    /// Represents the scope for persistent data.
    /// </summary>
    public enum PersistScope
    {
        /// <summary>
        /// Data is stored separately for each arena.
        /// </summary>
        PerArena,

        /// <summary>
        /// Data is stored globally (entire zone).
        /// </summary>
        Global,
    }

    /// <summary>
    /// Base class for a persistent data registration.
    /// </summary>
    /// <typeparam name="T"><see cref="Player"/> or <see cref="Arena"/></typeparam>
    public abstract class PersistentData<T>
    {
        /// <summary>
        /// Uniquely identifies the type of persistent data.
        /// </summary>
        /// <remarks>
        /// When adding another type of persistent data, use a value that is not used. See <see cref="PersistKey"/> for some known used keys to avoid.
        /// </remarks>
        public int Key { get; private set; }

        /// <summary>
        /// The interval to persist data for.
        /// </summary>
        public PersistInterval Interval { get; private set; }

        /// <summary>
        /// The scope of the data to persist.
        /// </summary>
        public PersistScope Scope { get; private set; }

        protected PersistentData(int key, PersistInterval interval, PersistScope scope)
        {
            Key = key;
            Interval = interval;
            Scope = scope;
        }

        /// <summary>
        /// Gets data that should be written to the persist database.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Implementors should write data about the <paramref name="target"/> to the <paramref name="outStream"/>.
        /// Not writing any data to <paramref name="outStream"/> means no persistent data should be saved for the <paramref name="target"/>, 
        /// and any existing data should be removed.
        /// </para>
        /// <para>
        /// This method is called on a worker thread. Remember to use thead sychronization techniques (e.g. locking) when accessing the data that's being written to <paramref name="outStream"/>.
        /// </para>
        /// </remarks>
        /// <param name="target">The target to get data for.</param>
        /// <param name="outStream">The stream to write the data to.</param>
        public abstract void GetData(T target, Stream outStream);

        /// <summary>
        /// Sets data that was retrieved from the persist database.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Implementors should read data about the <paramref name="target"/> from the <paramref name="inStream"/>.
        /// </para>
        /// <para>
        /// This method is called on a worker thread. Remember to use thead sychronization techniques (e.g. locking) when saving the data that's being read from <paramref name="inStream"/>.
        /// </para>
        /// </remarks>
        /// <param name="target">The target to set data for.</param>
        /// <param name="inStream">The stream to read the data from.</param>
        public abstract void SetData(T target, Stream inStream);

        /// <summary>
        /// Clears/resets the data being persisted for the <paramref name="target"/>.
        /// </summary>
        /// <param name="target">The target to clear/reset data for.</param>
        public abstract void ClearData(T target);
    }

    public class DelegatePersistentData<T> : PersistentData<T>
    {
        public delegate void GetDataDelegate(T target, Stream outStream);
        public GetDataDelegate GetDataCallback { get; init; }

        public delegate void SetDataDelegate(T target, Stream inStream);
        public SetDataDelegate SetDataCallback { get; init; }

        public delegate void ClearDataDelegate(T target);
        public ClearDataDelegate ClearDataCallback { get; init; }

        public DelegatePersistentData(int key, PersistInterval interval, PersistScope scope)
            : base(key, interval, scope)
        {
        }

        public DelegatePersistentData(int key, PersistInterval interval, PersistScope scope, 
            GetDataDelegate getDataCallback, SetDataDelegate setDataCallback, ClearDataDelegate clearDataCallback)
            : base(key, interval, scope)
        {
            GetDataCallback = getDataCallback;
            SetDataCallback = setDataCallback;
            ClearDataCallback = clearDataCallback;
        }

        public override void GetData(T target, Stream outStream) => GetDataCallback?.Invoke(target, outStream);

        public override void SetData(T target, Stream inStream) => SetDataCallback?.Invoke(target, inStream);

        public override void ClearData(T target) => ClearDataCallback?.Invoke(target);
    }

    public abstract class PersistentData<T, TState> : PersistentData<T>
    {
        protected TState State { get; }

        protected PersistentData(int key, PersistInterval interval, PersistScope scope, TState state) : base(key, interval, scope)
        {
            State = state;
        }
    }

    public class DelegatePersistentData<T, TState> : PersistentData<T, TState>
    {
        public delegate void GetDataDelegate(T target, Stream outStream, TState state);
        public GetDataDelegate GetDataCallback { get; init; }

        public delegate void SetDataDelegate(T target, Stream inStream, TState state);
        public SetDataDelegate SetDataCallback { get; init; }

        public delegate void ClearDataDelegate(T target, TState state);
        public ClearDataDelegate ClearDataCallback { get; init; }

        public DelegatePersistentData(int key, PersistInterval interval, PersistScope scope, TState state)
            : base(key, interval, scope, state)
        {
        }

        public DelegatePersistentData(int key, PersistInterval interval, PersistScope scope, TState state,
            GetDataDelegate getDataCallback, SetDataDelegate setDataCallback, ClearDataDelegate clearDataCallback)
            : base(key, interval, scope, state)
        {
            GetDataCallback = getDataCallback;
            SetDataCallback = setDataCallback;
            ClearDataCallback = clearDataCallback;
        }

        public override void GetData(T target, Stream outStream) => GetDataCallback?.Invoke(target, outStream, State);

        public override void SetData(T target, Stream inStream) => SetDataCallback?.Invoke(target, inStream, State);

        public override void ClearData(T target) => ClearDataCallback?.Invoke(target, State);
    }

    /// <summary>
    /// Interface for hooking into persistent storage services.
    /// </summary>
    public interface IPersist : IComponentInterface
    {
        /// <summary>
        /// Registers a slot of player persistent storage.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Registering with <see cref="PersistScope.Global"/> means: ClearData/SetData will be called on player login; GetData will be called on logoff.
        /// That is, the data is for a player, regardless of the arena the player is in.
        /// </para>
        /// <para>
        /// Registering with <see cref="PersistScope.PerArena"/> means: ClearData/SetData will be called when a player enters an arena; GetData will be called when a player leaves an arena.
        /// That is, the data is for a player, specifically for the arena the player is in.
        /// Keep in mind that some <see cref="PersistInterval"/>s allow data to be shared between designated arenas.
        /// Normally, this sharing is done based on an arena's <see cref="Arena.BaseName"/>. 
        /// For example: (Public 0), (Public 1), etc. will share scores for a <see cref="PersistInterval.Reset"/>.
        /// However, sharing can be manually overriden by specifying the General:ScoreGroup setting in an arena's configuration.
        /// Setting arenas with matching groups allow them to share data for <see cref="PersistInterval"/>s that allow shared data.
        /// </para>
        /// </remarks>
        /// <param name="registration">The registration to add.</param>
        void RegisterPersistentData(PersistentData<Player> registration);

        /// <summary>
        /// Unregisters a previously registered player persistent storage slot.
        /// </summary>
        /// <param name="registration">The registration to remove.</param>
        void UnregisterPersistentData(PersistentData<Player> registration);

        // TODO: Does registration of arena persistent data with PersistScope.Global make any sense?
        // Maybe arena registration shouldn't have scope as it should implicitly be PersistScope.PerArena?
        // When would the data be retrieved from the database?  When would the data be saved to the database?
        // For global data, the GetGeneric and PutGeneric methods would make sense.

        /// <summary>
        /// Registers a slot of arena persistent storage.
        /// </summary>
        /// <remarks>
        /// ClearData/SetData will be called when an arena is being created; GetData will be called when an arena is being destroyed.
        /// </remarks>
        /// <param name="registration">The registration to add.</param>
        void RegisterPersistentData(PersistentData<Arena> registration);

        /// <summary>
        /// Unregisters a previously registered arena persistent storage slot.
        /// </summary>
        /// <param name="registration">The registration to remove.</param>
        void UnregisterPersistentData(PersistentData<Arena> registration);
    }

    /// <summary>
    /// Interface for telling the <see cref="Modules.Persist"/> module to execute certain tasks.
    /// </summary>
    /// <remarks>
    /// This is used by core modules to tell the perist module to do work at very particular times.
    /// Most modules will likely not need to use this, unless they need to end an interval.
    /// </remarks>
    public interface IPersistExecutor : IComponentInterface
    {
        // TODO: Consider making these methods Async (return Task) instead of callback?
        // The caller could wait on the Task to complete (e.g. main thread when shutting down)
        // or could ContinueWith() to execute logic that should happen on completion (e.g. Core module and ArenaManager modules).
        // Consider allocations, with this callback approach, the work items are pooled objects.
        // With Task there will be an allocation. Is it possible to use ValueTask with pooled IValueTaskSource objects?
        // How would the caller use it? It would need to await whereas currently it just knows the callback eventually will get executed on the mainloop thread.

        #region Player methods

        /// <summary>
        /// Adds a request to save a <see cref="Player"/>'s persistent data to the database.
        /// That is, get player data from each registered provider and save the data to the database.
        /// </summary>
        /// <remarks>
        /// This method does not block. It adds a request into a work queue which will be processed by a worker thread.
        /// </remarks>
        /// <param name="player">The player to save persistent data for.</param>
        /// <param name="arena">The arena to save persistent data for <see cref="PersistScope.PerArena"/>. <see langword="null"/> for <see cref="PersistScope.Global"/>.</param>
        /// <param name="callback">
        /// An optional callback to be called after the <paramref name="player"/>'s persistent data has been saved.
        /// It is guaranteed to be executed on the mainloop thread.
        /// </param>
        void PutPlayer(Player player, Arena arena, Action<Player> callback);

        /// <summary>
        /// Adds a request to get a <see cref="Player"/>'s persistent data from the database.
        /// That is, retrieve player data from the database and send it to each registered provider.
        /// </summary>
        /// <remarks>
        /// This method does not block. It adds a request into a work queue which will be processed by a worker thread.
        /// </remarks>
        /// <param name="player">The player to retrieve persistent data for.</param>
        /// <param name="arena">The arena to retrieve persistent data for <see cref="PersistScope.PerArena"/>. <see langword="null"/> for <see cref="PersistScope.Global"/>.</param>
        /// <param name="callback">
        /// An optional callback to be called after the <paramref name="player"/>'s persistent data has been retrieved.
        /// It is guaranteed to be executed on the mainloop thread.
        /// </param>
        void GetPlayer(Player player, Arena arena, Action<Player> callback);

        #endregion

        #region Arena methods

        /// <summary>
        /// Adds a request to save an <see cref="Arena"/>'s persistent data from the database.
        /// </summary>
        /// <remarks>
        /// This method does not block. It adds a request into a work queue which will be processed by a worker thread.
        /// </remarks>
        /// <param name="arena">The arena to save persistent data for.</param>
        /// <param name="callback">
        /// An optional callback to be called after the <paramref name="arena"/>'s persistent data has been saved.
        /// It is guaranteed to be executed on the mainloop thread.
        /// </param>
        void PutArena(Arena arena, Action<Arena> callback);

        /// <summary>
        /// Adds a request to retrieve an <see cref="Arena"/>'s persistent data from the database.
        /// </summary>
        /// <remarks>
        /// This method does not block. It adds a request into a work queue which will be processed by a worker thread.
        /// </remarks>
        /// <param name="arena">The arena to retrieve persistent data for.</param>
        /// <param name="callback">
        /// An optional callback to be called after the <paramref name="arena"/>'s persistent data has been retrieved.
        /// It is guaranteed to be executed on the mainloop thread.
        /// </param>
        void GetArena(Arena arena, Action<Arena> callback);

        #endregion

        #region EndInterval

        /// <summary>
        /// Adds a request to ends an interval.
        /// </summary>
        /// <remarks>
        /// This method is for callers that are aware of arena groups.
        /// For example, a billing module may want to end a <see cref="PersistInterval.Reset"/> for <see cref="Constants.ArenaGroup_Public"/>.
        /// <para>
        /// This method does not block. It adds a request into a work queue which will be processed by a worker thread.
        /// </para>
        /// </remarks>
        /// <param name="interval">The interval to end.</param>
        /// <param name="arenaGroupOrArenaName">The arena group or arena name to end the interval for. <see langword="null"/> means global data.</param>
        void EndInterval(PersistInterval interval, string arenaGroupOrArenaName);

        /// <summary>
        /// Adds a request to end an interval.
        /// </summary>
        /// <remarks>
        /// This method does not block. It adds a request into a work queue which will be processed by a worker thread.
        /// </remarks>
        /// <param name="interval">The interval to end.</param>
        /// <param name="arena">The arena to end the interval for. <see langword="null"/> means global data.</param>
        void EndInterval(PersistInterval interval, Arena arena);

        #endregion

        /// <summary>
        /// Resets the <see cref="PersistInterval.Game"/> interval in an arena.
        /// Data is cleared from any persist registrations and from the persist database.
        /// </summary>
        /// <remarks>
        /// This method does not block. It adds a request into a work queue which will be processed by a worker thread.
        /// </remarks>
        /// <param name="arena">The arena to reset the game interval in.</param>
        /// <param name="completed">A callback that will be executed after the interval is reset. This is guaranteed to be run on the <see cref="IMainloop"/>.</param>
        void ResetGameInterval(Arena arena, Action<Arena> completed);

        /// <summary>
        /// Adds a request to save all persistent data to the database.
        /// </summary>
        /// <remarks>
        /// This method does not block. It adds a request into a work queue which will be processed by a worker thread.
        /// </remarks>
        /// <param name="completed">An optional callback, to be called after all data has been saved.</param>
        void SaveAll(Action completed);
    }

    /// <summary>
    /// Interface for a service that provides data storage for the <see cref="Modules.Persist"/> module.
    /// </summary>
    /// <remarks>
    /// The idea behind this is that one could use a database of their choosing by implementing this interface.
    /// At the moment, it's implemented by <see cref="Modules.PersistSQLite"/> which uses a SQLite database.
    /// In theory, one could create an implementation to access other database types such as Berkeley DB (which ASSS uses), LMDB, etc.
    /// </remarks>
    public interface IPersistDatastore : IComponentInterface
    {
        /// <summary>
        /// This is called when the Persist module is loaded. It provides a time to create/initialize the database.
        /// </summary>
        /// <returns><see langword="true"/> on success. <see langword="false"/> on failure.</returns>
        bool Open();

        /// <summary>
        /// This is called when the Persist module is unloaded. It provides a time to perform any closing tasks. (e.g. ASSS does a 'sync' for Berkeley DB).
        /// </summary>
        /// <returns><see langword="true"/> on success. <see langword="false"/> on failure.</returns>
        bool Close();

        /// <summary>
        /// Creates a new interval for an arena group and makes it the "current" one.
        /// </summary>
        /// <param name="arenaGroup"></param>
        /// <param name="interval"></param>
        /// <returns></returns>
        bool CreateArenaGroupIntervalAndMakeCurrent(string arenaGroup, PersistInterval interval);

        /// <summary>
        /// Gets a player's persistent data.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="arenaGroup"></param>
        /// <param name="interval"></param>
        /// <param name="key"></param>
        /// <param name="outStream"></param>
        /// <returns></returns>
        bool GetPlayerData(Player player, string arenaGroup, PersistInterval interval, int key, Stream outStream);

        /// <summary>
        /// Sets a player's persistent data.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="arenaGroup"></param>
        /// <param name="interval"></param>
        /// <param name="key"></param>
        /// <param name="inStream"></param>
        /// <returns></returns>
        bool SetPlayerData(Player player, string arenaGroup, PersistInterval interval, int key, MemoryStream inStream);

        /// <summary>
        /// Deletes a player's persistent data.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="arenaGroup"></param>
        /// <param name="interval"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        bool DeletePlayerData(Player player, string arenaGroup, PersistInterval interval, int key);

        /// <summary>
        /// Get an arena's persistent data.
        /// </summary>
        /// <param name="arenaGroup"></param>
        /// <param name="interval"></param>
        /// <param name="key"></param>
        /// <param name="outStream"></param>
        /// <returns></returns>
        bool GetArenaData(string arenaGroup, PersistInterval interval, int key, Stream outStream);

        /// <summary>
        /// Sets an arena's persistent data.
        /// </summary>
        /// <param name="arenaGroup"></param>
        /// <param name="interval"></param>
        /// <param name="key"></param>
        /// <param name="inStream"></param>
        /// <returns></returns>
        bool SetArenaData(string arenaGroup, PersistInterval interval, int key, MemoryStream inStream);
        
        /// <summary>
        /// Deletes an arena's persistent data.
        /// </summary>
        /// <param name="arenaGroup"></param>
        /// <param name="interval"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        bool DeleteArenaData(string arenaGroup, PersistInterval interval, int key);

        /// <summary>
        /// Clears an arena's <see cref="PersistInterval.Game"/> interval data.
        /// </summary>
        /// <param name="arenaGroup"></param>
        /// <returns></returns>
        bool ResetGameInterval(string arenaGroup);
    }
}

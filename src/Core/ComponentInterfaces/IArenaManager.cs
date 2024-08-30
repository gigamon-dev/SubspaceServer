using Microsoft.Extensions.ObjectPool;
using SS.Utilities.Collections;
using System;
using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    public interface IArenaManager : IComponentInterface
    {
        /// <summary>
        /// Locks the global arena lock.
        /// </summary>
        /// <remarks><see cref="Unlock"/> must be called when done.</remarks>
        void Lock();

        /// <summary>
        /// Unlocks the global arena lock.
        /// </summary>
        void Unlock();

        /// <summary>
        /// All the arenas the server knows about.
        /// </summary>
        /// <remarks>
        /// Remember to use <see cref="Lock"/> and <see cref="Unlock"/>.
        /// </remarks>
        Dictionary<string, Arena>.ValueCollection Arenas { get; } // ideally this would be IEnumerable<Arena>, but exposing the underlying type allows the compiler to use the enumerable struct rather than box it

        /// <summary>
        /// Recycles an arena by suspending all the players, unloading and
        /// reloading the arena, and then letting the players back in.
        /// </summary>
        /// <param name="arena"></param>
        /// <returns></returns>
        bool RecycleArena(Arena arena);

        /// <summary>
        /// Moves a player into a specific arena.
        /// </summary>
        /// <remarks>
        /// The <paramref name="player"/> must be a Continuum, VIE, or Chat client.
        /// </remarks>
        /// <param name="player">The player to move.</param>
        /// <param name="arenaName">The arena to send the <paramref name="player"/> to.</param>
        /// <param name="spawnX">The x-coordinate the <paramref name="player"/> should spawn at, or 0 for default.</param>
        /// <param name="spawnY">The y-coordinate the <paramref name="player"/> should spawn at, or 0 for default.</param>
        void SendToArena(Player player, ReadOnlySpan<char> arenaName, int spawnX, int spawnY);

        /// <summary>
        /// Tries to find an arena.
        /// </summary>
        /// <remarks>
        /// This only includes arenas that are in the <see cref="ArenaState.Running"/> state.
        /// </remarks>
        /// <param name="name">The name of the arena to find.</param>
        /// <returns>The arena if found. Otherwise, <see langword="null"/>.</returns>
        Arena? FindArena(ReadOnlySpan<char> name);

        /// <summary>
        /// Counts the number of players on the server and in each arena.
        /// This updates the player counts of every arena which can be accessed using <see cref="Arena.GetPlayerCounts(out int, out int)"/>.
        /// </summary>
        /// <param name="total">The total number of players.</param>
        /// <param name="playing">The number of players playing (not in spectator mode).</param>
        void GetPopulationSummary(out int total, out int playing);

        /// <summary>
        /// Allocates a slot for per-arena data.
        /// </summary>
        /// <remarks>
        /// This adds an instance of <typeparamref name="T"/> in each <see cref="Arena"/> object that can be accessed using <see cref="Arena.TryGetExtraData{T}(ArenaDataKey{T}, out T)"/>.
        /// <para>
        /// If <typeparamref name="T"/> implements <see cref="IResettable"/>, an object pool is used.
        /// </para>
        /// <para>
        /// If <typeparamref name="T"/> implements <see cref="IDisposable"/>, objects will get disposed when they are discarded.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The type to store in the slot.</typeparam>
        /// <returns>A key that can be used to access the data using <see cref="Arena.TryGetExtraData{T}(ArenaDataKey{T}, out T)"/>.</returns>
        ArenaDataKey<T> AllocateArenaData<T>() where T : class, new();

        /// <summary>
        /// Allocates a slot for per-arena data.
        /// </summary>
        /// <remarks>
        /// This adds an instance of <typeparamref name="T"/> in each <see cref="Arena"/> object that can be accessed using <see cref="Arena.TryGetExtraData{T}(ArenaDataKey{T}, out T)"/>.
        /// <para>
        /// If <typeparamref name="T"/> implements <see cref="IDisposable"/>, objects will get disposed when they are discarded.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The type to store in the slot.</typeparam>
        /// <param name="policy">The policy to use for object pooling.</param>
        /// <returns>A key that can be used to access the data using <see cref="Arena.TryGetExtraData{T}(ArenaDataKey{T}, out T)"/>.</returns>
        /// <exception cref="ArgumentNullException">The <paramref name="policy"/> was <see langword="null"/>.</exception>
        ArenaDataKey<T> AllocateArenaData<T>(IPooledObjectPolicy<T> policy) where T : class;

        /// <summary>
        /// Allocates a slot for per-arena data using an existing object pool.
        /// </summary>
        /// <remarks>
        /// This adds an instance of <typeparamref name="T"/> in each <see cref="Arena"/> object that can be accessed using <see cref="Arena.TryGetExtraData{T}(ArenaDataKey{T}, out T)"/>.
        /// </remarks>
        /// <typeparam name="T">The type to store in the slot.</typeparam>
        /// <param name="pool">The object pool to use.</param>
        /// <returns>A key that can be used to access the data using <see cref="Arena.TryGetExtraData{T}(ArenaDataKey{T}, out T)"/>.</returns>
        /// <exception cref="ArgumentNullException">The <paramref name="pool"/> was <see langword="null"/>.</exception>
        ArenaDataKey<T> AllocateArenaData<T>(ObjectPool<T> pool) where T : class;

        /// <summary>
        /// Frees a per-arena data slot.
        /// </summary>
        /// <param name="key">The key from <see cref="AllocateArenaData{T}"/>.</param>
        /// <returns><see langword="true"/> if the slot for given <paramref name="key"/> was freed. <see langword="false"/> if the <paramref name="key"/> was invalid.</returns>
        bool FreeArenaData<T>(ref ArenaDataKey<T> key);

        /// <summary>
        /// Adds a "hold" on an arena, preventing it from proceeding to the next stage in the arena life-cycle, until the hold is removed.
        /// </summary>
        /// <remarks>
        /// This can be used to do time-consuming work asynchronously during certain steps in the arena life-cycle.
        /// It may only be used in <see cref="ComponentCallbacks.ArenaActionCallback"/> handlers, and
        /// only for <see cref="ArenaAction.PreCreate"/>, <see cref="ArenaAction.Create"/>, and <see cref="ArenaAction.Destroy"/>.
        /// </remarks>
        /// <param name="arena">The arena to add a hold on.</param>
        void AddHold(Arena arena);

        /// <summary>
        /// Removes a "hold" on an arena.
        /// </summary>
        /// <remarks>
        /// This must be called exactly once for each time <see cref="AddHold(Arena)"/> is called. 
        /// It may be called from any thread.
        /// </remarks>
        /// <param name="arena">The arena to remove a hold from.</param>
        void RemoveHold(Arena arena);

        /// <summary>
        /// Collection of arena names that are present in the arenas directory.
        /// </summary>
        /// <remarks>
        /// Only directories that contain a file named "arena.conf" will be included. For example if the
        /// file "arenas/foo/arena.conf" is found, this list will contain a "foo" entry.
        /// "(default)" and any directory beginning with a dot are not included. "(public)" IS included.
        /// <para>Remember to use <see cref="Lock"/> and <see cref="Unlock"/>.</para>
        /// </remarks>
        ReadOnlyTrie KnownArenaNames { get; }
    }

    /// <summary>
    /// Interface to be used internally by the <see cref="Modules.Core"/> and <see cref="Modules.PlayerData"/> module.
    /// </summary>
    internal interface IArenaManagerInternal : IArenaManager
    {
        /// <summary>
        /// Tells the player that they're entering an arena.
        /// </summary>
        /// <remarks>
        /// This should only be called at the appropriate time from the <see cref="Modules.Core"/> module.
        /// </remarks>
        /// <param name="player">The player to send data to.</param>
        void SendArenaResponse(Player player);

        /// <summary>
        /// Tells the player that he's leaving an arena.
        /// </summary>
        /// <remarks>
        /// This should only be called at the appropriate time from the <see cref="Modules.PlayerData"/> module.
        /// </remarks>
        /// <param name="player"></param>
        void LeaveArena(Player player);
    }
}

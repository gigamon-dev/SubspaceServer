using Microsoft.Extensions.ObjectPool;
using System;
using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    public interface IPlayerData : IComponentInterface
    {
        void Lock();
        void Unlock();
        void WriteLock();
        void WriteUnlock();

        /// <summary>
        /// Use to enumerate over all of the players.
        /// </summary>
        /// <remarks>
        /// Remember to use <see cref="Lock"/> and <see cref="Unlock"/> or <see cref="WriteLock"/> and <see cref="WriteUnlock"/>.
        /// </remarks>
        Dictionary<int, Player>.ValueCollection Players { get; } // ideally this would be IEnumerable<Player>, but exposing the underlying type allows the compiler to use the enumerable struct rather than box it

        /// <summary>
        /// Creates a new player.
        /// This is called by the network modules when they get a new connection.
        /// </summary>
        /// <param name="clientType">the type of player to create</param>
        /// <returns></returns>
        Player NewPlayer(ClientType clientType);

        /// <summary>
        /// Frees memory associated with a player.
        /// This is called by the network modules when a connection has terminated.
        /// </summary>
        /// <param name="player">the player to free</param>
        void FreePlayer(Player player);

        /// <summary>
        /// Disconnects a player from the server.
        /// This does most of the work of disconnecting a player. The
        /// player's state will be transitioned to TimeWait, at which point
        /// one of the network modules must take responsibility for final cleanup.
        /// </summary>
        /// <param name="player">the player to kick</param>
        void KickPlayer(Player player);

        /// <summary>
        /// Finds the player with the given pid.
        /// </summary>
        /// <param name="pid">the pid to find</param>
        /// <returns>the player with the given pid, or null if not found</returns>
        Player PidToPlayer(int pid);

        /// <summary>
        /// Finds the player with the given name.
        /// The name is matched case-insensitively.
        /// </summary>
        /// <param name="name">The name to match.</param>
        /// <returns>The player with the given name, or <see langword="null"/> if not found.</returns>
        Player FindPlayer(ReadOnlySpan<char> name);

        /// <summary>
        /// Converts an <see cref="ITarget"/> to a specific list of players.
        /// The players represented by the target will be added to the given set.
        /// </summary>
        /// <param name="target">The target to convert.</param>
        /// <param name="set">The set to add players to.</param>
        void TargetToSet(ITarget target, HashSet<Player> set);

        /// <summary>
        /// Allocates a slot for per-player data.
        /// This creates a new instance of <typeparamref name="T"/> in each <see cref="Player"/> object.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If <typeparamref name="T"/> implements <see cref="IPooledExtraData"/>, an object pool is used.
        /// </para>
        /// <para>
        /// If <typeparamref name="T"/> implements <see cref="System.IDisposable"/>, objects will get disposed when they are discarded.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The type of data to store in the slot.</typeparam>
        /// <returns>A key that can be used to access the data using <see cref="Player.TryGetExtraData{T}(PlayerDataKey{T}, out T)"/>.</returns>
        PlayerDataKey<T> AllocatePlayerData<T>() where T : class, new();

        /// <summary>
        /// Allocates a slot for per-player data.
        /// </summary>
        /// <remarks>
        /// <para>
        /// If <typeparamref name="T"/> implements <see cref="System.IDisposable"/>, objects will get disposed when they are discarded.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The type of data to store in the slot.</typeparam>
        /// <param name="policy">The policy to use for object pooling.</param>
        /// <returns>A key that can be used to access the data using <see cref="Player.TryGetExtraData{T}(PlayerDataKey{T}, out T)"/>.</returns>
        /// <exception cref="ArgumentNullException">The policy was <see langword="null"/>.</exception>
        PlayerDataKey<T> AllocatePlayerData<T>(IPooledObjectPolicy<T> policy) where T : class;

        /// <summary>
        /// Frees a per-player data slot.
        /// </summary>
        /// <typeparam name="T">The type of data to store in the slot.</typeparam>
        /// <param name="key">The key from <see cref="AllocatePlayerData{T}"/>.</param>
        /// <returns><see langword="true"/> if the slot for given <paramref name="key"/> was freed. <see langword="false"/> if the <paramref name="key"/> was invalid.</returns>
        bool FreePlayerData<T>(ref PlayerDataKey<T> key) where T : class;
    }
}

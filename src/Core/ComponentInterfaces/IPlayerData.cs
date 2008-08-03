using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="p">the player being allocated/deallocated</param>
    /// <param name="isNew">true if being allocated, false if being deallocated</param>
    /// <returns></returns>
    public delegate void NewPlayerDelegate(Player p, bool isNew);

    public interface IPlayerData : IComponentInterface
    {
        void Lock();
        void Unlock();
        void WriteLock();
        void WriteUnlock();

        /// <summary>
        /// Use to enumerate over all of the players.
        /// Rember to Lock() and Unlock().
        /// </summary>
        IEnumerable<Player> PlayerList
        {
            get;
        }

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
        /// <param name="name">the name to match</param>
        /// <returns>the player with the given name, or NULL if not found</returns>
        Player FindPlayer(string name);

        /// <summary>
        /// Converts a Target to a specific list of players.
        /// The players represented by the target will be added to the given list.
        /// </summary>
        /// <param name="target">the target to convert</param>
        /// <param name="set">the list to add players to</param>
        void TargetToSet(ITarget target, out LinkedList<Player> set);

        // per player data
        int AllocatePlayerData<T>() where T : new();
        void FreePlayerData(int key);
    }
}

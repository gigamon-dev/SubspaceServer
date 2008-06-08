using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
        /// Rember to Lock() and Unlock().
        /// </summary>
        IEnumerable<Player> PlayerList
        {
            get;
        }

        Player NewPlayer(ClientType clientType);
        void FreePlayer(Player player);
        void KickPlayer(Player player);
        Player PidToPlayer(int pid);
        Player FindPlayer(string name);
        void TargetToSet(Target target, out LinkedList<Player> list);

        // per player data
        int AllocatePlayerData<T>() where T : new();
        void FreePlayerData(int key);
    }
}

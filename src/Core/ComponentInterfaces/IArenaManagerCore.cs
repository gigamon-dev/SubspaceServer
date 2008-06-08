using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public interface IArenaManagerCore : IComponentInterface
    {
        /// <summary>
        /// Locks the global arena lock.
        /// There is a lock protecting the arena list, which you need to hold
        /// whenever you access ArenaList. 
        /// Call this before you start, and Unlock() when you're done.
        /// </summary>
        void Lock();

        /// <summary>
        /// Unlocks the global arena lock.
        /// Use this whenever you used Lock()
        /// </summary>
        void Unlock();

        void SendArenaResponse(Player player);
        void LeaveArena(Player player);
        bool RecycleArena(Arena arena);
        void SendToArena(Player p, string arenaName, int spawnx, int spawny);

        Arena FindArena(string name);
        Arena FindArena(string name, out int totalCount, out int playing);
        void GetPopulationSummary(out int total, out int playing);

        int AllocateArenaData<T>() where T : new();
        void FreeArenaData(int key);

        void HoldArena(Arena arena);
        void UnholdArena(Arena arena);
    }
}

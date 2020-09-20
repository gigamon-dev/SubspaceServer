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

        /// <summary>
        /// Tells the player that he's entering an arena.
        /// This should only be called at the appropriate time from the core module.
        /// 
        /// TODO: move this into a separate interface
        /// </summary>
        /// <param name="player"></param>
        void SendArenaResponse(Player player);
        
        /// <summary>
        /// Tells the player that he's leaving an arena.
        /// This should only be called at the appropriate time from the core module.
        /// 
        /// TODO: move this into a separate interface
        /// </summary>
        /// <param name="player"></param>
        void LeaveArena(Player player);

        /// <summary>
        /// Recycles an arena by suspending all the players, unloading and
        /// reloading the arena, and then letting the players back in.
        /// </summary>
        /// <param name="arena"></param>
        /// <returns></returns>
        bool RecycleArena(Arena arena);

        /// <summary>
        /// Moves a player into a specific arena.
        /// Works on Continuum clients only.
        /// </summary>
        /// <param name="p">the player to move</param>
        /// <param name="arenaName">the arena to send him to</param>
        /// <param name="spawnx">the x coord he should spawn at, or 0 for default</param>
        /// <param name="spawny">the y coord he should spawn at, or 0 for default</param>
        void SendToArena(Player p, string arenaName, int spawnx, int spawny);

        /// <summary>
        /// This is a function for locating arenas.
        /// Given a name, it returns either an arena (if some arena by that
        /// name is running) or NULL (if not). 
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        Arena FindArena(string name);

        /// <summary>
        /// This is a multi-purpose function for locating and counting arenas.
        /// Given a name, it returns either an arena (if some arena by that
        /// name is running) or NULL (if not). If it's running, it also fills
        /// in the next two params with the number of players in the arena
        /// and the number of non-spec players in the arena.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        Arena FindArena(string name, out int totalCount, out int playing);

        /// <summary>
        /// This counts the number of players in the server and in each arena.
        /// It fills in its two parameters with population values for the
        /// whole server, and also fills in the total and playing fields of
        /// each Arena. You should be holding the arena lock when
        /// calling this.
        /// </summary>
        /// <param name="total"></param>
        /// <param name="playing"></param>
        void GetPopulationSummary(out int total, out int playing);

        /// <summary>
        /// Allocates space in the arena struct for per-arena data.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns>a key to be used in an Arena's indexer</returns>
        int AllocateArenaData<T>() where T : new();

        /// <summary>
        /// Frees per-arena space.
        /// </summary>
        /// <param name="key"></param>
        void FreeArenaData(int key);

        /// <summary>
        /// Puts a "hold" on an arena, preventing it from proceeding to the
        /// next stage in initialization until the hold is removed.
        /// This can be used to do some time-consuming work during arena
        /// creation asynchronously, e.g. in another thread. It may only be
        /// used in ArenaAction callbacks, only for ArenaAction.PreCreate,
        /// ArenaAction.Create and ArenaAction.Destroy actions.
        /// </summary>
        /// <param name="arena"></param>
        void HoldArena(Arena arena);

        /// <summary>
        /// Removes a "hold" on an arena.
        /// This must be called exactly once for each time Hold is called. It
        /// may be called from any thread.
        /// </summary>
        /// <param name="arena"></param>
        void UnholdArena(Arena arena);

        /// <summary>
        /// All the arenas the server knows about.
        /// Remember to use <see cref="Lock"/> and <see cref="Unlock"/>.
        /// </summary>
        IEnumerable<Arena> ArenaList { get; }
    }
}

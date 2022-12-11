using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// the arenaplace interface
    /// You should register an interface of this type if you want to control
    /// which arena players get placed in when they connect without a preference.
    /// </summary>
    public interface IArenaPlace : IComponentInterface
    {
        /// <summary>
        /// Place a player in an arena.
        /// This will be called when a player requests to join an arena
        /// without indicating any preference. To place the player, you
        /// should copy an arena name into the buffer pointed to by name
        /// and return true.  Optionally, put some spawn
        /// coordinates at the locations pointed to by spawnx and spawny.
        /// </summary>
        /// <param name="arenaName">to place the arena name in</param>
        /// <param name="spawnX">to put spawn coordinates, if desired</param>
        /// <param name="spawnY">to put spawn coordinates, if desired</param>
        /// <param name="player">the player being placed</param>
        /// <returns>true if name was filled in, false on any error</returns>
        bool Place(out string arenaName, ref int spawnX, ref int spawnY, Player player);
    }
}

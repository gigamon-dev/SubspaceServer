﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    public interface ICapabilityManager : IComponentInterface
    {
        /// <summary>
        /// Check if a player has a given capability.
        /// Some common capabilities are defined as macros at the end of this
        /// file. Capabilities for commands are all named "cmd_foo" for ?foo,
        /// and "privcmd_foo" for /?foo.
        /// </summary>
        /// <param name="p">the player to check</param>
        /// <param name="capability">the capability to check for</param>
        /// <returns>true if the player has the capability, otherwise false</returns>
        bool HasCapability(Player p, string capability);

        /// <summary>
        /// Check if a player has a given capability, using a name instead
        /// a player pointer.  This is intended to be used before the player's 
        /// name has been assigned. You shouldn't have to use this.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="capability"></param>
        /// <returns></returns>
        bool HasCapability(string name, string capability);

        /// <summary>
        /// Checks if a player has a given capability in an arena other than
        /// the one he's currently in.
        /// </summary>
        /// <param name="p">the player</param>
        /// <param name="a">the arena to check in</param>
        /// <param name="capability">the capability to check for</param>
        /// <returns>true if the player would have the requested capability, if he were in that arena</returns>
        bool HasCapability(Player p, Arena a, string capability);

        /// <summary>
        /// Determines if a player can perform actions on another player.
        /// For certain actions (e.g., /?kick), you need to know if a player
        /// is at a "higher" level than another. Use this function to tell.
        /// The exact meaning of "higher" is determined by the capability
        /// manger, but it should at least be transitive.
        /// </summary>
        /// <param name="a">a player</param>
        /// <param name="b">a player</param>
        /// <returns>true if a is higher than b, otherwise false</returns>
        bool HigherThan(Player a, Player b);
    }
}

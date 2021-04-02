using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for checking the capabilities of players.
    /// <para>
    /// Capabilities are used to determine the functionality that a user is authorized for.
    /// Each capabliity is identified by a unique name.
    /// <list type="bullet">
    /// <item>
    /// Capabilities are named cmd_&lt;command name&gt; for commands that are untargeted (that is, typed as a public message).
    /// For example, "cmd_foo" for ?foo.
    /// </item>
    /// <item>
    /// Capabilities are named privcmd_&lt;command name&gt; for commands directed at a player or team (private or team messages).
    /// For example, "privcmd_bar" for /?bar, //?bar, '?bar, or "?bar
    /// </item>
    /// <item>
    /// Capabilities used to determine if a user in one group is authorized to perform an action 
    /// on another group are named "higher_than_&lt;group name&gt;". For example, a group can 
    /// perform actions on the "mod" group if it has the "higher_than_mod" capability.    
    /// </item>
    /// <item>
    /// For other capability names see <see cref="Constants.Capabilities"/>
    /// </item>
    /// </list>
    /// </para>
    /// </summary>
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

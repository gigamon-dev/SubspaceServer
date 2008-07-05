using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// you probably shouldn't use this.
    /// </summary>
    public interface IGroupManager : IComponentInterface
    {
        /// <summary>
        /// Returns the group that the player is currently in.
        /// The group might change if the player moves to another arena.
        /// </summary>
        /// <param name="p"></param>
        /// <returns></returns>
        string GetGroup(Player p);

        /// <summary>
        /// Changes a player's group (permanently).
        /// </summary>
        /// <param name="p"></param>
        /// <param name="group"></param>
        /// <param name="global"></param>
        /// <param name="info"></param>
        void SetPermGroup(Player p, string group, bool global, string info);

        /// <summary>
        /// Changes a players group (temporarily, until arena change).
        /// </summary>
        /// <param name="p"></param>
        /// <param name="group"></param>
        void SetTempGroup(Player p, string group);

        /// <summary>
        /// Changes a player's group back to the default (permanently).
        /// </summary>
        /// <param name="p"></param>
        /// <param name="info"></param>
        void RemoveGroup(Player p, string info);

        /// <summary>
        /// Checks if a group password is correct.
        /// </summary>
        /// <param name="group"></param>
        /// <param name="pwd"></param>
        /// <returns>true if pwd is the password for group, false if not</returns>
        bool CheckGroupPassword(string group, string pwd);
    }
}

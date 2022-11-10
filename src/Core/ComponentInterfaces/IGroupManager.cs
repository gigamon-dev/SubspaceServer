using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that manages groups for capabilities
    /// </summary>
    /// <remarks>
    /// You probably shouldn't use this.
    /// </remarks>
    public interface IGroupManager : IComponentInterface
    {
        /// <summary>
        /// Returns the group that the player is currently in.
        /// The group might change if the player moves to another arena.
        /// </summary>
        /// <param name="player"></param>
        /// <returns></returns>
        string GetGroup(Player player);

        /// <summary>
        /// Changes a player's group (permanently).
        /// </summary>
        /// <param name="player"></param>
        /// <param name="group"></param>
        /// <param name="global"></param>
        /// <param name="comment"></param>
        void SetPermGroup(Player player, ReadOnlySpan<char> group, bool global, string comment);

        /// <summary>
        /// Changes a players group (temporarily, until arena change).
        /// </summary>
        /// <param name="player"></param>
        /// <param name="group"></param>
        void SetTempGroup(Player player, ReadOnlySpan<char> group);

        /// <summary>
        /// Changes a player's group back to the default (permanently).
        /// </summary>
        /// <param name="player"></param>
        /// <param name="comment"></param>
        void RemoveGroup(Player player, string comment);

        /// <summary>
        /// Checks if a group password is correct.
        /// </summary>
        /// <param name="group"></param>
        /// <param name="pwd"></param>
        /// <returns>true if pwd is the password for group, false if not</returns>
        bool CheckGroupPassword(ReadOnlySpan<char> group, ReadOnlySpan<char> pwd);
    }
}

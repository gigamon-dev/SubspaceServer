using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;

namespace SS.Core.ComponentInterfaces
{
    public interface IKillPoints : IComponentInterface
    {
        /// <summary>
        /// To determine how many points should be awarded and what green prize be dropped when one player kills another.
        /// </summary>
        /// <param name="arena">the arena the kill happened in</param>
        /// <param name="killer">player that got the kill</param>
        /// <param name="killed">player that got killed</param>
        /// <param name="bounty">bounty of the player that got killed</param>
        /// <param name="transferFlags"># of flags that the player that got killed was holding at the time of the kill</param>
        /// <param name="totalPoints">points the killer will be awarded</param>
        /// <param name="green">prize that will be left at the location the player that got killed</param>
        void GetKillPoints(Arena arena, Player killer, Player killed, int bounty, int transferFlags, out short totalPoints, out Prize green);
    }
}

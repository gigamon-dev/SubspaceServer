using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// this interface is designed to be implemented by a non-core module,
    /// and probably registered per-arena (as a result of attaching a module
    /// to an arena). its functions are then called by core modules when a
    /// player's ship/freq need to be changed for any reason. they will see
    /// the player whose ship/freq is being changed, and the requested ship
    /// and freq. they may modify the requested ship and freq. if the freq
    /// isn't determined yet (as for InitialFreq, freq will contain -1).
    /// if you want to deny the change request, just set ship and freq to the
    /// same as the current values:
    /// <code>
    /// ship = p.Ship;
    /// freq = p.Freq;
    /// </code>
    /// </summary>
    public interface IFreqManager : IComponentInterface
    {
        /// <summary>
        /// called when a player connects and needs to be assigned to a freq.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="ship">will initially contain the requested ship</param>
        /// <param name="freq">will initially contain -1</param>
        void InitialFreq(Player p, ref ShipType ship, ref short freq);

        /// <summary>
        /// called when a player requests a ship change.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="ship">will initially contain the requested ship</param>
        /// <param name="freq">will initially contain -1</param>
        void ShipChange(Player p, ref ShipType ship, ref short freq);

        /// <summary>
        /// called when a player requests a freq change.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="ship">will initially contain the requested ship</param>
        /// <param name="freq">will initially contain -1</param>
        void FreqChange(Player p, ref ShipType ship, ref short freq);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SS.Core.ComponentInterfaces
{
    public interface IFake : IComponentInterface
    {
        /// <summary>
        /// Creates a fake player.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="arena"></param>
        /// <param name="ship"></param>
        /// <param name="freq"></param>
        /// <returns></returns>
        Player CreateFakePlayer(ReadOnlySpan<char> name, Arena arena, ShipType ship, short freq);

        /// <summary>
        /// Removes a fake player.
        /// </summary>
        /// <param name="player"></param>
        /// <returns></returns>
        bool EndFaked(Player player);
    }
}

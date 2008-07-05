using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;

namespace SS.Core.ComponentInterfaces
{
    public interface IGame : IComponentInterface
    {
        void SetFreq(Player p, short freq);

        void SetShip(Player p, ShipType ship);

        void SetFreqAndShip(Player p, ShipType ship, short freq);

        void WarpTo(Target target, short x, short y);

        void GivePrize(Target target, Prize prizeType, short count);

        void Lock(Target target, bool notify, bool spec, int timeout);

        void Unlock(Target target, bool notify);

        void LockArena(Arena arena, bool notify, bool onlyArenaState, bool initial, bool spec);

        void UnlockArena(Arena arena, bool notify, bool onlyArenaState);

        // TODO: more

        float GetIgnoreWeapons(Player p);

        void SetIgnoreWeapons(Player p, float proportion);

        // TODO: more
    }
}

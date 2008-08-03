using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;

namespace SS.Core.ComponentInterfaces
{
    public interface IGame : IComponentInterface
    {
        /// <summary>
        /// Changes a player's freq.
        /// This is an unconditional change; it doesn't go through the freq manager.
        /// </summary>
        /// <param name="p">the player to change</param>
        /// <param name="freq">the freq to change to</param>
        void SetFreq(Player p, short freq);

        /// <summary>
        /// Changes a player's ship.
        /// This is an unconditional change; it doesn't go through the freq manager.
        /// </summary>
        /// <param name="p">the player to change</param>
        /// <param name="ship">the ship to change to</param>
        void SetShip(Player p, ShipType ship);

        /// <summary>
        /// Changes a player's ship and freq together.
        /// This is an unconditional change; it doesn't go through the freq manager.
        /// </summary>
        /// <param name="p">the player to change</param>
        /// <param name="ship">the ship to change to</param>
        /// <param name="freq">the freq to change to</param>
        void SetFreqAndShip(Player p, ShipType ship, short freq);

        /// <summary>
        /// Moves a set of players to a specific location.
        /// This uses the Continuum warp feature, so it causes the little
        /// flashy thing, and the affected ships won't be moving afterwards.
        /// </summary>
        /// <param name="target">the players to warp</param>
        /// <param name="x">the destination of the warp in tiles, x coordinate</param>
        /// <param name="y">the destination of the warp in tiles, y coordinate</param>
        void WarpTo(ITarget target, short x, short y);

        /// <summary>
        /// Gives out prizes to a set of players.
        /// </summary>
        /// <param name="target">the players to give prizes to</param>
        /// <param name="prizeType">the type of the prizes to give, or 0 for random</param>
        /// <param name="count">the number of prizes to give</param>
        void GivePrize(ITarget target, Prize prizeType, short count);

        /// <summary>
        /// Locks a set of players to their ship and freq.
        /// Note that this doesn't modify the default lock state for the
        /// arena, so this will not affect newly entering players at all. Use
        /// LockArena for that.
        /// </summary>
        /// <param name="target">the players to lock</param>
        /// <param name="notify">whether to notify the affected players (with an arena message) if their lock status has changed</param>
        /// <param name="spec">whether to spec them before locking them to their ship/freq</param>
        /// <param name="timeout">the number of seconds the lock should be effective for, or zero for indefinite.</param>
        void Lock(ITarget target, bool notify, bool spec, int timeout);

        /// <summary>
        /// Undoes the effect of Lock, allowing players to change ship and freq again.
        /// Again, note that this doesn't affect the default lock state for the arena.
        /// </summary>
        /// <param name="target">the players to unlock</param>
        /// <param name="notify">whether to notify the affected players if their lock status changed</param>
        void Unlock(ITarget target, bool notify);

        /// <summary>
        /// Locks all players in the arena to spectator mode, or to their current ships.
        /// This modifies the arena lock state, and also has the effect of calling Lock on all the players in the arena.
        /// </summary>
        /// <param name="arena">the arena to apply changes to</param>
        /// <param name="notify">whether to notify affected players of their change in state</param>
        /// <param name="onlyArenaState">whether to apply changes to the default arena lock state only, and not change the state of current players</param>
        /// <param name="initial">whether entering players are only locked to their inital ships, rather than being forced into spectator mode and then being locked</param>
        /// <param name="spec">whether to force all current players into spec before locking them</param>
        void LockArena(Arena arena, bool notify, bool onlyArenaState, bool initial, bool spec);

        /// <summary>
        /// Undoes the effect of LockArena by changing the arena lock state and unlocking current players.
        /// </summary>
        /// <param name="arena">the arena to apply changes to</param>
        /// <param name="notify">whether to notify affected players of their change in state</param>
        /// <param name="onlyArenaState">whether to apply changes to the default</param>
        void UnlockArena(Arena arena, bool notify, bool onlyArenaState);

        // TODO: more

        /// <summary>
        /// Gets the percentage of weapons that are being ignored for a given player.
        /// </summary>
        /// <param name="p">player to get info about</param>
        /// <returns></returns>
        float GetIgnoreWeapons(Player p);

        /// <summary>
        /// Sets the percentage of weapons to ignore for a given player.
        /// </summary>
        /// <param name="p">player to set</param>
        /// <param name="proportion">percentage of weapons packets to ignore</param>
        void SetIgnoreWeapons(Player p, float proportion);

        /// <summary>
        /// Resets the target's ship(s).
        /// </summary>
        /// <param name="target">the players to shipreset</param>
        void ShipReset(ITarget target);

        // TODO: more
    }
}

using SS.Packets.Game;
using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    public enum SeeEnergy
    {
        /// <summary>
        /// Nobody can see energy
        /// </summary>
        None,

        /// <summary>
        /// everyone can see everyone's
        /// </summary>
        All,

        /// <summary>
        /// you can see only energy for teammates
        /// </summary>
        Team,

        /// <summary>
        /// can see energy/extra data only for who you are speccing
        /// </summary>
        Spec,
    }

    public interface IGame : IComponentInterface
    {
        /// <summary>
        /// Changes a player's freq.
        /// This is an unconditional change; it doesn't go through the freq manager.
        /// </summary>
        /// <param name="player">the player to change</param>
        /// <param name="freq">the freq to change to</param>
        void SetFreq(Player player, short freq);

        /// <summary>
        /// Changes a player's ship.
        /// This is an unconditional change; it doesn't go through the freq manager.
        /// </summary>
        /// <param name="player">the player to change</param>
        /// <param name="ship">the ship to change to</param>
        void SetShip(Player player, ShipType ship);

        /// <summary>
        /// Changes a player's ship and freq together.
        /// This is an unconditional change; it doesn't go through the freq manager.
        /// </summary>
        /// <param name="player">the player to change</param>
        /// <param name="ship">the ship to change to</param>
        /// <param name="freq">the freq to change to</param>
        void SetShipAndFreq(Player player, ShipType ship, short freq);

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
        /// Checks if a player is locked to their ship/freq.
        /// </summary>
        /// <param name="player">The player to check.</param>
        /// <returns>Whether the player is locked.</returns>
        bool HasLock(Player player);

        /// <summary>
        /// Locks all players in the arena to spectator mode, or to their current ships.
        /// This modifies the arena lock state, and also has the effect of calling Lock on all the players in the arena.
        /// </summary>
        /// <param name="arena">the arena to apply changes to</param>
        /// <param name="notify">whether to notify affected players of their change in state</param>
        /// <param name="onlyArenaState">whether to apply changes to the default arena lock state only, and not change the state of current players</param>
        /// <param name="initial">whether entering players are only locked to their initial ships, rather than being forced into spectator mode and then being locked</param>
        /// <param name="spec">whether to force all current players into spec before locking them</param>
        void LockArena(Arena arena, bool notify, bool onlyArenaState, bool initial, bool spec);

        /// <summary>
        /// Undoes the effect of LockArena by changing the arena lock state and unlocking current players.
        /// </summary>
        /// <param name="arena">the arena to apply changes to</param>
        /// <param name="notify">whether to notify affected players of their change in state</param>
        /// <param name="onlyArenaState">whether to apply changes to the default</param>
        void UnlockArena(Arena arena, bool notify, bool onlyArenaState);

        /// <summary>
        /// Checks if there is an arena-wide ship/freq lock in effect.
        /// </summary>
        /// <param name="arena">The arena to check.</param>
        /// <returns>Whether there is an arena-wide lock.</returns>
        bool HasLock(Arena arena);

		/// <summary>
		/// Processes a C2S position packet for a player, almost as if a regular C2S position packet were received.
		/// </summary>
		/// <remarks>
		/// This method is meant as a way to process position packets for fake players.
		/// For example, for the replay module to process position packets read from a recording, or for an AI player module to send position packets.
		/// However, it could also be used to fake packets for regular players too.
		/// </remarks>
		/// <param name="player">The player to process the position packet for.</param>
		/// <param name="pos">The position packet to process.</param>
		void FakePosition(Player player, ref C2S_PositionPacket pos);

		/// <summary>
		/// Processes a C2S position packet for a player, almost as if a regular C2S position packet were received.
		/// </summary>
		/// <remarks>
		/// This method is meant as a way to process position packets for fake players.
		/// For example, for the replay module to process position packets read from a recording, or for an AI player module to send position packets.
		/// However, it could also be used to fake packets for regular players too.
		/// </remarks>
		/// <param name="player">The player to process the position packet for.</param>
		/// <param name="pos">The position packet to process.</param>
        /// <param name="extra">The extra position data to process.</param>
		void FakePosition(Player player, ref C2S_PositionPacket pos, ref ExtraPositionData extra);

		/// <summary>
		/// Processes a kill event, almost as if a a C2S die packet were received.
		/// </summary>
		/// <remarks>
		/// This method is meant as a way to send packets for fake players.
		/// For example, for the replay module to process kill events, or for an AI player module to send kill events.
		/// However, it could also be used to fake packets for regular players too.
		/// </remarks>
		/// <param name="killer">The player that made the kill.</param>
		/// <param name="killed">The player that was killed.</param>
		/// <param name="pts">The number of points awarded.</param>
		/// <param name="flags">The number of carryable flags transferred.</param>
		void FakeKill(Player killer, Player killed, short pts, short flags);

        /// <summary>
        /// Gets the percentage of weapons that are being ignored for a given player.
        /// </summary>
        /// <param name="player">player to get info about</param>
        /// <returns></returns>
        double GetIgnoreWeapons(Player player);

        /// <summary>
        /// Sets the percentage of weapons to ignore for a given player.
        /// </summary>
        /// <param name="player">player to set</param>
        /// <param name="proportion">percentage of weapons packets to ignore</param>
        void SetIgnoreWeapons(Player player, double proportion);

        /// <summary>
        /// Resets the target's ship(s).
        /// </summary>
        /// <param name="target">the players to shipreset</param>
        void ShipReset(ITarget target);

        /// <summary>
        /// Increments the count of S2C weapon packets that were sent to a player.
        /// </summary>
        /// <param name="player">The player to increment the count for.</param>
        /// <param name="packets">The amount to increment by.</param>
        void IncrementWeaponPacketCount(Player player, int packets);

        /// <summary>
        /// Sets whether a player can see other players energy.
        /// </summary>
        /// <param name="player">The player to set.</param>
        /// <param name="value">Whether energy can be seen, and if so, of whom.</param>
        void SetPlayerEnergyViewing(Player player, SeeEnergy value);

		/// <summary>
		/// Sets whether a player, when spectating, can see other players energy.
		/// </summary>
		/// <param name="player">The player to set.</param>
		/// <param name="value">Whether energy can be seen, and if so, of whom.</param>
		void SetSpectatorEnergyViewing(Player player, SeeEnergy value);

        /// <summary>
        /// Resets whether a player can see other players energy, to the default for the player.
        /// </summary>
        /// <param name="player">The player to reset.</param>
        void ResetPlayerEnergyViewing(Player player);

        /// <summary>
        /// Resets whether a player, when spectating, can see other players energy, to the default for the player.
        /// </summary>
        /// <param name="player">The player to reset.</param>
        void ResetSpectatorEnergyViewing(Player player);

        /// <summary>
        /// Adds a module-level extra position data watch on a player.
        /// </summary>
        /// <remarks>
        /// This will tell the player's client to send extra position data if it already isn't.
        /// The calling module can register for the <see cref="ComponentCallbacks.PlayerPositionPacketCallback"/> and read the extra position data from it.
        /// <para>
        /// Remember to call <see cref="RemoveExtraPositionDataWatch(Player)"/> when done.
        /// </para>
        /// </remarks>
        /// <param name="player">The player to add a watch on.</param>
        void AddExtraPositionDataWatch(Player player);

        /// <summary>
        /// Removes a module-level extra position data watch from a player.
        /// </summary>
        /// <param name="player">The player to remove a watch on.</param>
        void RemoveExtraPositionDataWatch(Player player);

        /// <summary>
        /// Gets whether a player is being antiwarped by another player.
        /// </summary>
        /// <param name="player">The player to check.</param>
        /// <param name="playersAntiwarping">An optional list to populate with the players that are antiwarping.</param>
        /// <returns>True if antiwarped, false otherwise.</returns>
        bool IsAntiwarped(Player player, HashSet<Player> playersAntiwarping);

        /// <summary>
        /// Forcefully attach a player to another player.
        /// </summary>
        /// <remarks>Note that continuum is not able to handle going over the TurretLimit.</remarks>
        /// <param name="player">The attacher.</param>
        /// <param name="to">The player to attach to.</param>
        void Attach(Player player, Player to);
    }
}

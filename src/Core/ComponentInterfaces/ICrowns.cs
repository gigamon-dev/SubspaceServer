using SS.Packets.Game;
using System;
using System.Collections.Generic;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for the <see cref="Modules.Crowns"/> module.
    /// It keeps track of which players have a crown by setting the <see cref="S2C_PlayerData.HasCrown"/> of <see cref="Player.Packet"/>.
    /// Other modules can look at the value, but should not modify it directly. Instead, call methods on this interface to do the modification.
    /// </summary>
    public interface ICrowns : IComponentInterface
    {
        /// <summary>
        /// Toggles every player in an arena to have a crown.
        /// </summary>
        /// <param name="arena">The arena to set crowns for.</param>
        /// <param name="duration">The duration of the crown. <see cref="TimeSpan.Zero"/> for a crown that doesn't expire.</param>
        void ToggleOn(Arena arena, TimeSpan duration);

        /// <summary>
        /// Toggles a set of players to have a crown.
        /// </summary>
        /// <param name="players"></param>
        /// <param name="duration"></param>
        void ToggleOn(HashSet<Player> players, TimeSpan duration);

        /// <summary>
        /// Toggles a player to have a crown.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="duration"></param>
        void ToggleOn(Player player, TimeSpan duration);

        /// <summary>
        /// Adds time to a player's crown. The player must already have a crown.
        /// </summary>
        /// <remarks>
        /// The client's timer has a maximum based on the duration it was given when the crown was toggled on or last set (<see cref="TrySetTime(Player, TimeSpan)"/>).
        /// </remarks>
        /// <param name="player"></param>
        /// <param name="additional"></param>
        /// <returns></returns>
        bool TryAddTime(Player player, TimeSpan additional);

        /// <summary>
        /// Updates a player's crown duration. The player must already have a crown.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="duration"></param>
        /// <returns></returns>
        bool TrySetTime(Player player, TimeSpan duration);

        /// <summary>
        /// Toggles every player in the arena to not have a crown.
        /// </summary>
        /// <param name="arena"></param>
        void ToggleOff(Arena arena);

        /// <summary>
        /// Toggles a set of players to not have a crown.
        /// </summary>
        /// <param name="players"></param>
        void ToggleOff(HashSet<Player> players);

        /// <summary>
        /// Toggles a player to not have a crown.
        /// </summary>
        /// <param name="player"></param>
        void ToggleOff(Player player);
    }

    /// <summary>
    /// Interface for extending crown behavior.
    /// </summary>
    /// <remarks>
    /// The default implementation will just remove the player's crown.
    /// <para>
    /// The <see cref="Modules.Scoring.Koth"/> module keeps track of crown expirations and expires 
    /// crowns for players with matching expired times simultaneously in order to determine a winner.
    /// </para>
    /// </remarks>
    public interface ICrownsBehavior : IComponentInterface
    {
        /// <summary>
        /// Handler for when the servers receives a <see cref="C2SPacketType.CrownExpired"/> packet.
        /// </summary>
        /// <param name="player">The player that send the crown expiration packet.</param>
        void CrownExpired(Player player);
    }
}

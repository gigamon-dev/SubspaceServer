using System;
using System.Text;

namespace SS.Core.ComponentAdvisors
{
    [Flags]
    public enum ShipMask
    {
        None = 0,
        Warbird = 1,
        Javelin = 2,
        Spider = 4, 
        Leviathan = 8, 
        Terrier = 16, 
        Weasel = 32, 
        Lancaster = 64,
        Shark = 128,
        All = 255,
    }

    public static class ShipMaskExtensions
    {
        /// <summary>
        /// Translates a <see cref="ShipType"/> into a <see cref="ShipMask"/>.
        /// </summary>
        /// <param name="ship">The ship type.</param>
        /// <returns>The ship mask.</returns>
        public static ShipMask GetShipMask(this ShipType ship)
        {
            return ship switch
            {
                ShipType.Warbird => ShipMask.Warbird,
                ShipType.Javelin => ShipMask.Javelin,
                ShipType.Spider => ShipMask.Spider,
                ShipType.Leviathan => ShipMask.Leviathan,
                ShipType.Terrier => ShipMask.Terrier,
                ShipType.Weasel => ShipMask.Weasel,
                ShipType.Lancaster => ShipMask.Lancaster,
                ShipType.Shark => ShipMask.Shark,
                _ => ShipMask.None,
            };
        }

        /// <summary>
        /// Checks if a mask includes a specific ship.
        /// </summary>
        /// <param name="mask">The mask to check.</param>
        /// <param name="ship">The ship to look for.</param>
        /// <returns>True if the ship is in the mask. Otherwise, false.</returns>
        public static bool HasShip(this ShipMask mask, ShipType ship)
        {
            return (mask & ship.GetShipMask()) != 0;
        }
    }

    /// <summary>
    /// Interface for an advisor on rules to enforce for <see cref="Modules.FreqManager"/>.
    /// </summary>
    public interface IFreqManagerEnforcerAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Gets the ships a player is allowed to use.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="ship"></param>
        /// <param name="freq"></param>
        /// <param name="errorMessage"></param>
        /// <returns>A mask containing flags describing the allowed ships.</returns>
        ShipMask GetAllowableShips(Player player, ShipType ship, short freq, StringBuilder errorMessage) => ShipMask.All;

        /// <summary>
        /// Checks whether a player can switch to a new freq.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="newFreq"></param>
        /// <param name="errorMessage"></param>
        /// <returns></returns>
        bool CanChangeToFreq(Player player, short newFreq, StringBuilder errorMessage) => true;

        /// <summary>
        /// Checks whether a player can enter the game at all. 
        /// </summary>
        /// <remarks>
        /// This is called before the frequency they're landing on has been decided,
        /// so <see cref="Player.Freq"/> shoudl not be looked at.
        /// This is only called if the player is in spectator mode.
        /// </remarks>
        /// <param name="player"></param>
        /// <param name="errorMessage"></param>
        /// <returns></returns>
        bool CanEnterGame(Player player, StringBuilder errorMessage) => true;

        /// <summary>
        /// Checks whether a player can change from their current ship/or freq.
        /// </summary>
        /// <param name="player"></param>
        /// <param name="errorMessage"></param>
        /// <returns></returns>
        bool IsUnlocked(Player player, StringBuilder errorMessage) => true;
    }
}

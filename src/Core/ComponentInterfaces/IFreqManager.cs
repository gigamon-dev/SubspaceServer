using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that manages freq and ship changes for players.
    /// </summary>
    public interface IFreqManager : IComponentInterface
    {
        /// <summary>
        /// Called when a player connects and needs to be assigned to a freq.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="ship">will initially contain the requested ship</param>
        /// <param name="freq">will initially contain -1</param>
        void Initial(Player p, ref ShipType ship, ref short freq);

        /// <summary>
        /// Called when a player requests a ship change.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="requestedShip">will initially contain the requested ship</param>
        /// <param name="errorMessage"></param>
        void ShipChange(Player p, ShipType requestedShip, StringBuilder errorMessage);

        /// <summary>
        /// Called when a player requests a freq change.
        /// </summary>
        /// <param name="p"></param>
        /// <param name="requestedShip">will initially contain the requested ship</param>
        /// <param name="errorMessage"></param>
        void FreqChange(Player p, short requestedFreqNum, StringBuilder errorMessage);
    }

    /// <summary>
    /// Interface for a service that defines how teams should be balanced, 
    /// but leaves the actual balancing logic to <see cref="Modules.FreqManager"/>.
    /// </summary>
    public interface IFreqBalancer : IComponentInterface
    {
        /// <summary>
        /// Gets a player's balance metric.
        /// </summary>
        /// <remarks>
        /// <see cref="Modules.FreqManager"/> uses this to ensure the teams are balanced.
        /// </remarks>
        /// <param name="p"></param>
        /// <returns></returns>
        int GetPlayerMetric(Player p);

        /// <summary>
        /// Gets a team's maximum balance metric.
        /// </summary>
        /// <param name="arena"></param>
        /// <param name="freq"></param>
        /// <returns></returns>
        int GetMaxMetric(Arena arena, short freq);

        /// <summary>
        /// Gets the maximum difference in metric allowed between two teams.
        /// </summary>
        /// <remarks>
        /// This operation should be commutative. That is, it should return the same value if <paramref name="freq1"/> or <paramref name="freq2"/> are interchanged.
        /// </remarks>
        /// <param name="arena"></param>
        /// <param name="freq1"></param>
        /// <param name="freq2"></param>
        /// <returns></returns>
        int GetMaximumDifference(Arena arena, short freq1, short freq2);
    }
}

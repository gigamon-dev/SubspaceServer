using SS.Packets.Game;

namespace SS.Core.ComponentAdvisors
{
    /// <summary>
    /// Advisor interface for the <see cref="Modules.Bricks"/> module.
    /// </summary>
    public interface IBricksAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Asks whether a brick should be sent to a player.
        /// </summary>
        /// <remarks>
        /// The brick data will not be sent to the player if any advisor says it's not valid.
        /// </remarks>
        /// <param name="player">The player under consideration for sending the brick data to.</param>
        /// <param name="brickData">The brick data.</param>
        /// <returns><see langword="true"/> if the <paramref name="brickData"/> is valid for the <paramref name="player"/>; otherwise, <see langword="false"/></returns>
        bool IsValidForPlayer(Player player, ref readonly BrickData brickData);
    }
}

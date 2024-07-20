using SS.Packets;

namespace SS.Core.ComponentInterfaces
{
    public interface IBanners : IComponentInterface
    {
        /// <summary>
        /// Gets a copy of the player's current banner.
        /// </summary>
        /// <param name="player">The player to get the banner for.</param>
        /// <param name="banner">The banner.</param>
        /// <returns><see langword="true"/> if the player had a banner (not necessearily in use). Otherwise, <see langword="false"/>.</returns>
        bool TryGetBanner(Player player, out Banner banner);

        /// <summary>
        /// Sets the banner for a player.
        /// </summary>
        /// <param name="player">The player to set the banner for.</param>
        /// <param name="banner">The banner to set.</param>
        void SetBanner(Player player, ref readonly Banner banner);

        /// <summary>
        /// Checks whether a player has a banner waiting to be used.
        /// If so, it will check whether the player meets the criteria to use a banner
        /// by querying <see cref="ComponentAdvisors.IBannersAdvisor.IsAllowedBanner(Player)"/>.
        /// If so, it will send the banner to all players in the arena.
        /// </summary>
        /// <remarks>
        /// An <see cref="ComponentAdvisors.IBannersAdvisor"/> implementation would call this method 
        /// if it detects that its condition for allowing a banner has changed.
        /// </remarks>
        /// <param name="player">The player to check the banner of.</param>
        void CheckAndSendBanner(Player player);
    }
}

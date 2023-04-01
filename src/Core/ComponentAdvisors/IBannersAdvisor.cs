namespace SS.Core.ComponentAdvisors
{
    /// <summary>
    /// Interface for an advisor on banners.
    /// </summary>
    public interface IBannersAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Gets whether a player is allowed to use a banner.
        /// </summary>
        /// <remarks>
        /// The banner system will disallow a player from using a banner if any advisor returns <see langword="false"/>.
        /// </remarks>
        /// <param name="player">The player to check.</param>
        /// <returns><see langword="true"/> if the player is allowed to set a banner. Otherwise, <see langword="false"/>.</returns>
        bool IsAllowedBanner(Player player) => true;
    }
}

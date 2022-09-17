namespace SS.Core.ComponentAdvisors
{
    /// <summary>
    /// Interface for an advisor to the statistics module.
    /// </summary>
    public interface IStatsAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Get the name of a statistic.
        /// </summary>
        /// <remarks>
        /// Modules that define custom statistics can use this to provide a user friendly names for printing out in the ?stats command.
        /// </remarks>
        /// <param name="statId">The statistic to get a name for.</param>
        /// <returns>The name or null if unknown.</returns>
        string GetStatName(int statId) => null;
    }
}

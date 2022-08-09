namespace SS.Core.ComponentInterfaces
{
    public interface ISecuritySeedSync : IComponentInterface
    {
        /// <summary>
        /// Gets the current seed info (server-wide).
        /// </summary>
        /// <returns></returns>
        void GetCurrentSeedInfo(out uint greenSeed, out uint doorSeed, out uint timestamp);

        /// <summary>
        /// Overrides the seeds for an arena.
        /// </summary>
        /// <param name="arena">The arena to override seeds on.</param>
        /// <param name="greenSeed">The seed for greens (prizes).</param>
        /// <param name="doorSeed">The seed for doors.</param>
        /// <param name="timestamp">The timestamp of the seed.</param>
        void OverrideArenaSeedInfo(Arena arena, uint greenSeed, uint doorSeed, uint timestamp);

        /// <summary>
        /// Removes an arena's override.
        /// </summary>
        /// <param name="arena">The arena to remove seed the override from.</param>
        /// <returns>Whether an override was removed.</returns>
        bool RemoveArenaOverride(Arena arena);
    }
}

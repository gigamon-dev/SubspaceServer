namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that manages a jackpot per arena.
    /// </summary>
    public interface IJackpot : IComponentInterface
    {
        /// <summary>
        /// Resets the jackpot for an arena.
        /// </summary>
        /// <param name="arena">The arena to reset the jackpot for.</param>
        void ResetJackpot(Arena arena);

        /// <summary>
        /// Adds points to the jackpot for an arena.
        /// </summary>
        /// <param name="arena">The arena to add jackpot points for.</param>
        /// <param name="points">The points to add.</param>
        void AddJackpot(Arena arena, int points);

        /// <summary>
        /// Gets the jackpot for an arena.
        /// </summary>
        /// <param name="arena">The arena to get the jackpot for.</param>
        /// <returns>The value of the jackpot.</returns>
        int GetJackpot(Arena arena);

        /// <summary>
        /// Sets the jackpot for an arena.
        /// </summary>
        /// <param name="arena">The arena to set tha jackpot for.</param>
        /// <param name="points">The value to set the jackpot to.</param>
        void SetJackpot(Arena arena, int points);
    }
}

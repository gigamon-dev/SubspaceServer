using System;

namespace SS.Core.ComponentInterfaces
{
    public interface IBallGamePoints : IComponentInterface
    {
        /// <summary>
        /// Resets the ball game.
        /// </summary>
        /// <param name="arena">The arena to reset the ball game in.</param>
        /// <param name="player">Optional, player that initiated the reset.</param>
        void ResetGame(Arena arena, Player player);

        /// <summary>
        /// Gets the ball game's scores.
        /// </summary>
        /// <param name="arena">The arena to get ball game scores for.</param>
        /// <returns>The scores.</returns>
        ReadOnlySpan<int> GetScores(Arena arena);

        /// <summary>
        /// Sets the scores for the ball game.
        /// </summary>
        /// <param name="arena">The arena to set ball game scores for.</param>
        /// <param name="scores">The scores to set.</param>
        void SetScores(Arena arena, ReadOnlySpan<int> scores);

        /// <summary>
        /// Resets scores for the ball game without ending the ball game.
        /// </summary>
        /// <param name="arena">The arena to reset ball game scores for.</param>
        void ResetScores(Arena arena);
    }
}

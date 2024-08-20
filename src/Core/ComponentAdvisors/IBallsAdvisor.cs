using SS.Core.ComponentInterfaces;
using SS.Core.Map;

namespace SS.Core.ComponentAdvisors
{
    /// <summary>
    /// Interface for an advisor on ball related activities.
    /// </summary>
    public interface IBallsAdvisor : IComponentAdvisor
    {
        /// <summary>
        /// Checks whether to allow a player to pick up a ball.
        /// </summary>
        /// <param name="arena">The arena.</param>
        /// <param name="player">The player.</param>
        /// <param name="ballId">The ball.</param>
        /// <param name="ballData">The ball data.</param>
        /// <returns><see langword="true"/> to allow the pickup. <see langword="false"/> to disallow the pickup.</returns>
        bool AllowPickupBall(Arena arena, Player player, int ballId, ref BallData ballData) => true;

        /// <summary>
        /// Checks whether to allow a player to shoot a ball.
        /// </summary>
        /// <param name="arena">The arena.</param>
        /// <param name="player">The player.</param>
        /// <param name="ballId">The ball.</param>
        /// <param name="isForced">Whether the client is firing the ball or the module is forcing a ball fire.</param>
        /// <param name="ballData">The ball data.</param>
        /// <returns>
        /// <see langword="true"/> to allow the shot.
        /// <see langword="false"/> to disallow the shot, causing the ball to be restuck to the player.</returns>
        bool AllowShootBall(Arena arena, Player player, int ballId, bool isForced, ref BallData ballData) => true;

        /// <summary>
        /// Checks whether to allow a goal to be scored.
        /// </summary>
        /// <param name="arena">The arena.</param>
        /// <param name="player">The player.</param>
        /// <param name="ballId">The ball.</param>
        /// <param name="coordinates">The coordinates.</param>
        /// <param name="ballData">The ball data.</param>
        /// <returns>
        /// <see langword="true"/> to allow the goal to be scored.</returns>
        /// <see langword="false"/> to disallow the goal. Note that Continuum will continue sending goal packets several times in this case.
        /// </returns>
        bool AllowGoal(Arena arena, Player player, int ballId, TileCoordinates coordinates, ref BallData ballData) => true;
    }
}

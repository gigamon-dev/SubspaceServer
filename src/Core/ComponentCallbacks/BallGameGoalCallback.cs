using SS.Core.Map;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a goal is scored in a ball game.
    /// </summary>
    /// <remarks>
    /// The difference between <see cref="BallGoalCallback"/> and <see cref="BallGameGoalCallback"/> is that 
    /// <see cref="BallGameGoalCallback"/> occurs later, after ball game scores have been updated.
    /// </remarks>
    /// <param name="arena">The arena the goal occured in.</param>
    /// <param name="player">The player that scored the goal.</param>
    /// <param name="ballId">The ball that was scored.</param>
    /// <param name="goalCoordinates">The coordinates of the goal that the ball was scored in.</param>
    [ComponentCallback]
    public delegate void BallGameGoalCallback(Arena arena, Player player, byte ballId, TileCoordinates goalCoordinates);
}

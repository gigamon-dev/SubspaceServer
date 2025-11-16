namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when a player:
    /// <list type="bullet">
    /// <item>shoots a ball they're carrying</item>
    /// <item>is killed while carrying a ball</item>
    /// <item>leaves while carrying a ball</item>
    /// <item>changes ship/freq while carrying a ball</item>
    /// </list>
    /// </summary>
    /// <param name="arena">The arena the ball event occured in.</param>
    /// <param name="player">The player that shot the ball.</param>
    /// <param name="ballId">ID of the ball that was shot.</param>
    [ComponentCallback]
    public delegate void BallShootCallback(Arena arena, Player player, byte ballId);
}

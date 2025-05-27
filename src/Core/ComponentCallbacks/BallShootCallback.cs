namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback for when a player:
    /// <list type="bullet">
    /// <item>shoots a ball they're carrying</item>
    /// <item>is killed while carrying a ball</item>
    /// <item>leaves while carrying a ball</item>
    /// <item>changes ship/freq while carrying a ball</item>
    /// </list>
    /// </summary>
    // TODO: Maybe rename this to BallPosessionLostCallback?
    [CallbackHelper]
    public static partial class BallShootCallback
    {
        public delegate void BallShootDelegate(Arena arena, Player player, byte ballId);
    }
}

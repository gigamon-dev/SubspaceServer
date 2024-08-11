using SS.Core.ComponentInterfaces;

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
    public static class BallShootCallback
    {
        public delegate void BallShootDelegate(Arena arena, Player player, byte ballId);

        public static void Register(IComponentBroker broker, BallShootDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, BallShootDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena, Player player, byte ballId)
        {
            broker?.GetCallback<BallShootDelegate>()?.Invoke(arena, player, ballId);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, player, ballId);
        }
    }
}

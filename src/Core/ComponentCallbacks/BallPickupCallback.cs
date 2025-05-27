namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class BallPickupCallback
    {
        public delegate void BallPickupDelegate(Arena arena, Player player, byte ballId);
    }
}

namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class BallCountChangedCallback
    {
        public delegate void BallCountChangedDelegate(Arena arena, int newCount, int oldCount);
    }
}

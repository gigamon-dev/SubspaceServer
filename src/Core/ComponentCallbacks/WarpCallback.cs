namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class WarpCallback
    {
        public delegate void WarpDelegate(Player player, int oldX, int oldY, int newX, int newY);
    }
}

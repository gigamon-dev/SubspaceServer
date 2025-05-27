using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="PersistIntervalEndedDelegate"/> callback
    /// which is fired by the <see cref="Modules.Persist"/> module after it ends an interval.
    /// </summary>
    [CallbackHelper]
    public static partial class PersistIntervalEndedCallback
    {
        public delegate void PersistIntervalEndedDelegate(PersistInterval interval, string arenaGroup);
    }
}

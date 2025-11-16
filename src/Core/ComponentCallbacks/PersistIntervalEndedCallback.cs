using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when the <see cref="Modules.Persist"/> module has ended an interval.
    /// </summary>
    /// <param name="interval"></param>
    /// <param name="arenaGroup"></param>
    [ComponentCallback]
    public delegate void PersistIntervalEndedCallback(PersistInterval interval, string arenaGroup);
}

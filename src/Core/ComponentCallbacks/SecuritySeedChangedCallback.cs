using SS.Utilities;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Callback delegate for when the seed for green (prizes) and door timings has changed.
    /// </summary>
    /// <param name="greenSeed"></param>
    /// <param name="doorSeed"></param>
    /// <param name="timestamp"></param>
    [ComponentCallback]
    public delegate void SecuritySeedChangedCallback(uint greenSeed, uint doorSeed, ServerTick timestamp);
}

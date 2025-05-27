using SS.Utilities;

namespace SS.Core.ComponentCallbacks
{
    [CallbackHelper]
    public static partial class SecuritySeedChangedCallback
    {
        public delegate void SecuritySeedChangedDelegate(uint greenSeed, uint doorSeed, ServerTick timestamp);
    }
}

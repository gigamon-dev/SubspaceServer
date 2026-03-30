using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Matchmaking.Interfaces;

namespace SS.Matchmaking.Callbacks
{
    [CallbackHelper]
    public static partial class StatboxPreferenceChangedCallback
    {
        public delegate void StatboxPreferenceChangedDelegate(Player player, StatboxPreference newPreference);
    }
}

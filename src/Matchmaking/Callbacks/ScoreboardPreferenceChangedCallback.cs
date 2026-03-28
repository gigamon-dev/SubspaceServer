using SS.Core;
using SS.Core.ComponentCallbacks;
using SS.Matchmaking.Interfaces;

namespace SS.Matchmaking.Callbacks
{
    [CallbackHelper]
    public static partial class ScoreboardPreferenceChangedCallback
    {
        public delegate void ScoreboardPreferenceChangedDelegate(Player player, ScoreboardPreference newPreference);
    }
}

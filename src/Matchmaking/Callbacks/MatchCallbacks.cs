using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    [CallbackHelper]
    public static partial class MatchStartingCallback
    {
        public delegate void MatchStartingDelegate(IMatch match);
    }

    [CallbackHelper]
    public static partial class MatchStartedCallback
    {
        public delegate void MatchStartedDelegate(IMatch match);
    }

    [CallbackHelper]
    public static partial class MatchEndingCallback
    {
        public delegate void MatchEndingDelegate(IMatch match);
    }

    [CallbackHelper]
    public static partial class MatchEndedCallback
    {
        public delegate void MatchEndedDelegate(IMatch match);
    }

    [CallbackHelper]
    public static partial class MatchAddPlayingCallback
    {
        public delegate void MatchAddPlayingDelegate(IMatch match, string playerName, Player? player);
    }

    [CallbackHelper]
    public static partial class MatchRemovePlayingCallback
    {
        public delegate void MatchRemovePlayingDelegate(IMatch match, string playerName, Player? player);
    }

    [CallbackHelper]
    public static partial class MatchFocusChangedCallback
    {
        public delegate void MatchFocusChangedDelegate(Player player, IMatch? oldMatch, IMatch? newMatch);
    }
}

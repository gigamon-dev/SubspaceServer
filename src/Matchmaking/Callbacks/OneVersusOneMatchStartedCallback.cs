using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.Matchmaking.Callbacks
{
    /// <summary>
    /// Callback for when a 1v1 match starts.
    /// </summary>
    public static class OneVersusOneMatchStartedCallback
    {
        public delegate void OneVersusOneMatchStartedDelegate(Arena arena, int boxId, Player player1, Player player2);

        public static void Register(IComponentBroker broker, OneVersusOneMatchStartedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, OneVersusOneMatchStartedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena, int boxId, Player player1, Player player2)
        {
            broker?.GetCallback<OneVersusOneMatchStartedDelegate>()?.Invoke(arena, boxId, player1, player2);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, boxId, player1, player2);
        }
    }
}

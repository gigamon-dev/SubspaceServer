using SS.Core;
using SS.Core.ComponentInterfaces;

namespace SS.Matchmaking.Callbacks
{
    public enum OneVersusOneMatchEndReason
    {
        /// <summary>
        /// A winner was decided.
        /// </summary>
        Decided,

        /// <summary>
        /// Ended in a draw (Double knockout).
        /// </summary>
        Draw,

        /// <summary>
        /// Ended because one or both players gave up (change to spec, changed arenas, disconnected).
        /// </summary>
        Aborted,
    }

    /// <summary>
    /// Callback for when a 1v1 match ends.
    /// </summary>
    public static class OneVersusOneMatchEndedCallback
    {
        public delegate void OneVersusOneMatchEndedDelegate(Arena arena, int boxId, OneVersusOneMatchEndReason reason, string? winnerPlayerName);

        public static void Register(IComponentBroker broker, OneVersusOneMatchEndedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, OneVersusOneMatchEndedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena, int boxId, OneVersusOneMatchEndReason reason, string? winnerPlayerName)
        {
            broker?.GetCallback<OneVersusOneMatchEndedDelegate>()?.Invoke(arena, boxId, reason, winnerPlayerName);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, boxId, reason, winnerPlayerName);
        }
    }
}

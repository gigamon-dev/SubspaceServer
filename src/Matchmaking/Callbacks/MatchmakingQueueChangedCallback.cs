using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    public enum QueueAction
    {
        Add,
        Remove,
    }

    /// <summary>
    /// Callback for when there is a change to a matchmaking queue.
    /// </summary>
    [CallbackHelper]
    public static partial class MatchmakingQueueChangedCallback
    {
        public delegate void MatchmakingQueueChangedDelegate(IMatchmakingQueue queue, QueueAction action);
    }
}

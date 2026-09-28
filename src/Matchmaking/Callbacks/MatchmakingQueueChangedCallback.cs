using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    public enum QueueAction
    {
        Add,
        Remove,
    }

    /// <summary>
    /// Callback delegate for when there is a change to a matchmaking queue.
    /// </summary>
    /// <param name="queue"></param>
    /// <param name="action"></param>
    [ComponentCallback]
    public delegate void MatchmakingQueueChangedCallback(IMatchmakingQueue queue, QueueAction action);
}

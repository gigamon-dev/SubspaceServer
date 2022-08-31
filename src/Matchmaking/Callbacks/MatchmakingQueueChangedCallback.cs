using SS.Core;

namespace SS.Matchmaking.Callbacks
{
    public enum QueueAction
    {
        Add,
        Remove,
    }

    public enum QueueItemType
    {
        Player,
        Group,
    }

    /// <summary>
    /// Callback for when there is a change to a matchmaking queue.
    /// </summary>
    public static class MatchmakingQueueChangedCallback
    {
        public delegate void MatchmakingQueueChangedDelegate(IMatchmakingQueue queue, QueueAction action, QueueItemType itemType);

        public static void Register(ComponentBroker broker, MatchmakingQueueChangedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, MatchmakingQueueChangedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, IMatchmakingQueue queue, QueueAction action, QueueItemType itemType)
        {
            broker?.GetCallback<MatchmakingQueueChangedDelegate>()?.Invoke(queue, action, itemType);

            if (broker?.Parent != null)
                Fire(broker.Parent, queue, action, itemType);
        }
    }
}

using SS.Core.ComponentInterfaces;

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
    public static class MatchmakingQueueChangedCallback
    {
        public delegate void MatchmakingQueueChangedDelegate(IMatchmakingQueue queue, QueueAction action);

        public static void Register(IComponentBroker broker, MatchmakingQueueChangedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, MatchmakingQueueChangedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, IMatchmakingQueue queue, QueueAction action)
        {
            broker?.GetCallback<MatchmakingQueueChangedDelegate>()?.Invoke(queue, action);

            if (broker?.Parent != null)
                Fire(broker.Parent, queue, action);
        }
    }
}

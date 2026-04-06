using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Utilities.ObjectPool;

namespace SS.Matchmaking.Queues
{
    /// <summary>
    /// Base implementation of a matchmaking queue for solo player matches (1 player per team).
    /// </summary>
    public abstract class SoloMatchmakingQueue : IMatchmakingQueue
    {
        /// <summary>
        /// A pool of nodes for the <see cref="Queue"/>.
        /// </summary>
        protected static readonly DefaultObjectPool<LinkedListNode<QueuedPlayer>> QueuedPlayerNodePool = new(new LinkedListNodePooledObjectPolicy<QueuedPlayer>(), Constants.TargetPlayerCount);

        /// <summary>
        /// The player queue.
        /// </summary>
        protected readonly LinkedList<QueuedPlayer> Queue = new();

        public SoloMatchmakingQueue(
            string queueName,
            QueueOptions options,
            string? description)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(queueName);

            if (!options.AllowSolo)
                throw new ArgumentException("Must allow solo players.", nameof(options));

            if (options.AllowGroups)
                throw new ArgumentException("Cannot allow groups.", nameof(options));

            Name = queueName;
            Options = options;
            Description = description;
        }

        public string Name { get; }
        public QueueOptions Options { get; }
        public string? Description { get; }

        public bool Add(Player player, DateTime timestamp)
        {
            LinkedListNode<QueuedPlayer> newNode = QueuedPlayerNodePool.Get();
            newNode.ValueRef = new QueuedPlayer(player, timestamp);

            // Add the node in the proper order based on its timestamp.

            if (Queue.Count == 0 || Queue.Last!.ValueRef.Timestamp <= timestamp)
            {
                Queue.AddLast(newNode);
            }
            else
            {
                LinkedListNode<QueuedPlayer>? node = Queue.First;
                while (node is not null && node.ValueRef.Timestamp <= timestamp)
                {
                    node = node.Next;
                }

                if (node is not null)
                {
                    Queue.AddBefore(node, newNode);
                }
                else
                {
                    Queue.AddLast(newNode);
                }
            }

            return true;
        }

        public bool Add(IPlayerGroup group, DateTime timestamp)
        {
            // Groups are not supported.
            return false;
        }

        public bool Remove(Player player)
        {
            LinkedListNode<QueuedPlayer>? node = Queue.First;
            while (node is not null && node.ValueRef.Player != player)
            {
                node = node.Next;
            }

            if (node is null)
                return false;

            Queue.Remove(node);
            QueuedPlayerNodePool.Return(node);
            return true;
        }

        public bool Remove(IPlayerGroup group)
        {
            // Groups are not supported.
            return false;
        }

        public void GetQueued(ICollection<Player> soloPlayers, ICollection<IPlayerGroup> groups)
        {
            LinkedListNode<QueuedPlayer>? node = Queue.First;
            while (node is not null)
            {
                soloPlayers.Add(node.ValueRef.Player);
                node = node.Next;
            }
        }

        public bool TryGetPosition(Player? player, IPlayerGroup? group, out int position, out int totalEntries)
        {
            totalEntries = Queue.Count;
            position = 0;
            if (player is null)
                return false;

            int i = 0;
            LinkedListNode<QueuedPlayer>? node = Queue.First;
            while (node is not null)
            {
                i++;
                if (node.ValueRef.Player == player)
                {
                    position = i;
                    return true;
                }
                node = node.Next;
            }
            return false;
        }

        /// <summary>
        /// Represents a player in the queue and the timestamp that the player originally entered the queue.
        /// </summary>
        /// <param name="Player">The player.</param>
        /// <param name="Timestamp">The timestamp that the player entered the queue.</param>
        protected readonly record struct QueuedPlayer(Player Player, DateTime Timestamp);
    }
}

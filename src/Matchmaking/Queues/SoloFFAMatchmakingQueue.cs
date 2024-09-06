using SS.Core;

namespace SS.Matchmaking.Queues
{
    /// <summary>
    /// A matchmaking queue for solo Free For All (FFA) matches.
    /// </summary>
    public class SoloFFAMatchmakingQueue : SoloMatchmakingQueue
    {
        public SoloFFAMatchmakingQueue(
           string queueName,
           QueueOptions options,
           string? description,
           int minPlayers,
           int maxPlayers) : base(queueName, options, description)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(minPlayers, 2);

            if (maxPlayers < minPlayers)
                throw new ArgumentOutOfRangeException(nameof(maxPlayers), $"Cannot be less than {nameof(minPlayers)}.");

            MinPlayers = minPlayers;
            MaxPlayers = maxPlayers;
        }

        /// <summary>
        /// The minimum # of players required.
        /// </summary>
        public int MinPlayers { get; }

        /// <summary>
        /// The maximum # of players allowed.
        /// </summary>
        public int MaxPlayers { get; }

        /// <summary>
        /// Tries to get participants for a match.
        /// It will get as many players as there are available, up to <see cref="MaxPlayers"/>.
        /// </summary>
        /// <param name="players">The list of participants to fill in with players.</param>
        /// <returns><see langword="true"/> if there were enough players for a match, otherwise <see langword="false"/>.</returns>
        public bool GetParticipants(List<Player> players)
        {
            if (Queue.Count < MinPlayers)
                return false;

            while (players.Count < MaxPlayers)
            {
                LinkedListNode<QueuedPlayer>? node = Queue.First;
                if (node is null)
                    break;

                players.Add(node.ValueRef.Player);
                Queue.Remove(node);
                QueuedPlayerNodePool.Return(node);
            }

            return true;
        }
    }
}

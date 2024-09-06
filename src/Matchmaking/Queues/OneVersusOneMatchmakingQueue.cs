using SS.Core;
using System.Diagnostics.CodeAnalysis;

namespace SS.Matchmaking.Queues
{
    /// <summary>
    /// A matchmaking queue for 1v1 matches.
    /// </summary>
    public class OneVersusOneMatchmakingQueue(
        string queueName,
        QueueOptions options,
        string? description) : SoloMatchmakingQueue(queueName, options, description)
    {
        /// <summary>
        /// Gets the participants of a 1v1 match.
        /// </summary>
        /// <param name="player1">The first player.</param>
        /// <param name="player2">The second player.</param>
        /// <returns><see langword="true"/> if there were enough players to fill a match, otherwise <see langword="false"/>.</returns>
        public bool GetParticipants([MaybeNullWhen(false)] out Player player1, [MaybeNullWhen(false)] out Player player2)
        {
            if (Queue.Count < 2)
            {
                player1 = null;
                player2 = null;
                return false;
            }

            // Player 1
            LinkedListNode<QueuedPlayer> node = Queue.First!;
            player1 = node.ValueRef.Player;
            Queue.Remove(node);
            QueuedPlayerNodePool.Return(node);

            // Player 2
            node = Queue.First!;
            player2 = node!.ValueRef.Player;
            Queue.Remove(node);
            QueuedPlayerNodePool.Return(node);

            return true;
        }
    }
}

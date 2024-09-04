using Microsoft.Extensions.ObjectPool;
using SS.Core;
using SS.Utilities.ObjectPool;
using System.Diagnostics;

namespace SS.Matchmaking.TeamVersus
{
    /// <summary>
    /// Represents either a single <see cref="Core.Player"/> or a <see cref="IPlayerGroup"/> of players.
    /// </summary>
    public readonly record struct QueuedPlayerOrGroup
    {
        public QueuedPlayerOrGroup(Player player, DateTime timestamp)
        {
            Player = player ?? throw new ArgumentNullException(nameof(player));
            Group = null;
            Timestamp = timestamp;
        }

        public QueuedPlayerOrGroup(IPlayerGroup group, DateTime timestamp)
        {
            Player = null;
            Group = group ?? throw new ArgumentNullException(nameof(group));
            Timestamp = timestamp;
        }

        public Player? Player { get; }
        public IPlayerGroup? Group { get; }
        public DateTime Timestamp { get; }
    }

    /// <summary>
    /// A matchmaking queue for team games that can supports queuing up as a solo player or as a group of players (<see cref="IPlayerGroup"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The queue priorities matching up those that have been waiting the longest.
    /// </para>
    /// <para>
    /// Currently, the matching logic for groups is limited such that if a team requires N players, then only groups containing N players are supported.
    /// This was done to greatly simplify the matching logic, but could be upgraded to support groups of arbitrary sizes.
    /// Supporting groups of arbitrary sizes would require solving a variation of the multiple knapsack problem / bin packing problem
    /// where you need to find a combination that fills every team fully.
    /// </para>
    /// </remarks>
    public class TeamVersusMatchmakingQueue : IMatchmakingQueue 
    {
        private static readonly DefaultObjectPool<LinkedListNode<QueuedPlayerOrGroup>> s_nodePool = new(new LinkedListNodePooledObjectPolicy<QueuedPlayerOrGroup>(), Constants.TargetPlayerCount);
        private static readonly DefaultObjectPool<List<LinkedListNode<QueuedPlayerOrGroup>>> s_listPool = new(new ListPooledObjectPolicy<LinkedListNode<QueuedPlayerOrGroup>>() { InitialCapacity = Constants.TargetPlayerCount });

        private readonly LinkedList<QueuedPlayerOrGroup> _queue = new();

        public TeamVersusMatchmakingQueue(
            string queueName,
            QueueOptions options,
            string? description)
        {
            if (string.IsNullOrWhiteSpace(queueName))
                throw new ArgumentException("Cannot be null or white-space.", nameof(queueName));

            if (!options.AllowSolo && !options.AllowGroups)
                throw new ArgumentException($"At minimum {nameof(options.AllowSolo)} or {nameof(options.AllowGroups)} must be true.", nameof(options));

            Name = queueName;
            Options = options;
            Description = description;
        }

        public string Name { get; }
        public QueueOptions Options { get; }
        public string? Description { get; }

        #region Add

        public bool Add(Player player, DateTime timestamp)
        {
            ArgumentNullException.ThrowIfNull(player);

            LinkedListNode<QueuedPlayerOrGroup> node = s_nodePool.Get();
            node.ValueRef = new(player, timestamp);
            Add(node);
            return true;
        }

        public bool Add(IPlayerGroup group, DateTime timestamp)
        {
            ArgumentNullException.ThrowIfNull(group);

            LinkedListNode<QueuedPlayerOrGroup> node = s_nodePool.Get();
            node.ValueRef = new(group, timestamp);
            Add(node);
            return true;
        }

        private void Add(LinkedListNode<QueuedPlayerOrGroup> item)
        {
            if (_queue.Count == 0 || _queue.Last!.ValueRef.Timestamp <= item.ValueRef.Timestamp)
            {
                _queue.AddLast(item);
            }
            else
            {
                var node = _queue.First;
                while (node is not null && node.ValueRef.Timestamp <= item.ValueRef.Timestamp)
                {
                    node = node.Next;
                }

                if (node is not null)
                {
                    _queue.AddBefore(node, item);
                }
                else
                {
                    _queue.AddLast(item);
                }
            }
        }

        #endregion

        #region Remove

        public bool Remove(Player player)
        {
            LinkedListNode<QueuedPlayerOrGroup>? node = _queue.First;
            while (node is not null)
            {
                if (node.ValueRef.Player == player)
                {
                    _queue.Remove(node);
                    s_nodePool.Return(node);
                    return true;
                }

                node = node.Next;
            }

            return false;
        }

        public bool Remove(IPlayerGroup group)
        {
            LinkedListNode<QueuedPlayerOrGroup>? node = _queue.First;
            while (node is not null)
            {
                if (node.ValueRef.Group == group)
                {
                    _queue.Remove(node);
                    s_nodePool.Return(node);
                    return true;
                }

                node = node.Next;
            }

            return false;
        }

        #endregion

        /// <summary>
        /// Gets whether a player is in the queue.
        /// </summary>
        /// <param name="player">The player to search for.</param>
        /// <returns><see langword="true"/> if the player is in the queue; otherwise, <see langword="false"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="player"/> was null.</exception>
        public bool ContainsSoloPlayer(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);

            foreach (QueuedPlayerOrGroup pog in _queue)
            {
                if (pog.Player == player)
                    return true;
            }

            return false;
        }

        public void GetQueued(HashSet<Player> soloPlayers, HashSet<IPlayerGroup>? groups)
        {
            foreach (QueuedPlayerOrGroup pog in _queue)
            {
                if (pog.Player is not null)
                    soloPlayers?.Add(pog.Player);
                else if (pog.Group is not null)
                    groups?.Add(pog.Group);
            }
        }

        /// <summary>
        /// Gets participants for a team versus match.
        /// </summary>
        /// <param name="matchConfiguration">The match configuration.</param>
        /// <param name="teamList">A list of teams to fill with players.</param>
        /// <param name="participantList">A list to fill with all of the participants that were matched, in the order that they were queued.</param>
        /// <returns><see langword="true"/> if there were enough players to fill the teams for a match; otherwise, <see langword="false"/>.</returns>
        public bool GetParticipants(
            IMatchConfiguration matchConfiguration,
            IReadOnlyList<TeamLineup> teamList,
            List<Player> participantList)
        {
            ArgumentNullException.ThrowIfNull(matchConfiguration);
            ArgumentNullException.ThrowIfNull(teamList);
            ArgumentNullException.ThrowIfNull(participantList);

            int numTeams = matchConfiguration.NumTeams;
            int playersPerTeam = matchConfiguration.PlayersPerTeam;

            // TODO: logic is simplified when only allowing groups of the exact size, add support for other group sizes later
            if (playersPerTeam != Options.MinGroupSize || playersPerTeam != Options.MaxGroupSize)
            {
                return false;
            }

            Debug.Assert(numTeams == teamList.Count);

            foreach (TeamLineup team in teamList)
            {
                Debug.Assert(team.Players.Count == 0);
            }

            Debug.Assert(participantList.Count == 0);

            // Nodes of players that we formed a team with.
            // These nodes have been removed from the _queue linked list.
            // If in the end, we can't form enough teams, these will be added back into the _queue linked list.
            List<LinkedListNode<QueuedPlayerOrGroup>> pending = s_listPool.Get();

            // Nodes of solo players that we're trying to form a team with.
            // These nodes are still attached to the _queue linked list.
            // If we find enough players to fill a team, they will be removed from the _queue linked list 
            // and added to the pending list.
            List<LinkedListNode<QueuedPlayerOrGroup>> pendingSolo = s_listPool.Get();

            try
            {
                int premadeGroupId = 0;

                foreach (TeamLineup team in teamList)
                {
                    LinkedListNode<QueuedPlayerOrGroup>? node = _queue.First;
                    if (node is null)
                        break; // Cannot fill the team.

                    ref QueuedPlayerOrGroup pog = ref node.ValueRef;

                    if (pog.Group is not null)
                    {
                        // Got a group, which fills the team.
                        Debug.Assert(pog.Group.Members.Count == playersPerTeam);

                        premadeGroupId++;

                        foreach (Player player in pog.Group.Members)
                            team.Players.Add(player.Name!, premadeGroupId);

                        _queue.Remove(node);
                        pending.Add(node);
                        continue; // Filled the team with a group.
                    }
                    else if (pog.Player is not null)
                    {
                        // Got a solo player, check if there are enough solo players to fill the team.
                        pendingSolo.Add(node);

                        while (pendingSolo.Count < playersPerTeam
                            && (node = node!.Next) is not null)
                        {
                            pog = ref node.ValueRef;
                            if (pog.Player is not null)
                            {
                                pendingSolo.Add(node);
                            }
                        }

                        if (pendingSolo.Count == playersPerTeam)
                        {
                            // Found enough solo players to fill the team.
                            foreach (LinkedListNode<QueuedPlayerOrGroup> soloNode in pendingSolo)
                            {
                                pog = ref soloNode.ValueRef;
                                team.Players.Add(pog.Player!.Name!, null);
                                _queue.Remove(soloNode);
                                pending.Add(soloNode);
                            }

                            pendingSolo.Clear();
                            continue; // Filled the team with solo players.
                        }
                        else
                        {
                            // Did not find enough solo players to fill the team.
                            pendingSolo.Clear();

                            // Try to find a group to fill the team instead.
                            node = _queue.First!.Next;
                            while (node is not null)
                            {
                                pog = ref node.ValueRef;
                                if (pog.Group is not null)
                                    break;

                                node = node.Next;
                            }

                            if (node is not null && pog.Group is not null)
                            {
                                // Got a group, which fills the team.
                                Debug.Assert(pog.Group.Members.Count == playersPerTeam);

                                premadeGroupId++;

                                foreach (Player player in pog.Group.Members)
                                    team.Players.Add(player.Name!, premadeGroupId);

                                _queue.Remove(node);
                                pending.Add(node);
                                continue; // Filled the team with a group.
                            }

                            break; // Cannot fill the team.
                        }
                    }
                }

                // TODO: configuration setting to consider a successful matching to have a certain minimum # of filled teams? (e.g. a 2 player per team FFA match)
                // TODO: configuration setting to allow a partially filled team if it has a configured minimum # of players? (e.g. a battle royale style game that couldn't completely fill a team but has enough teams total)

                bool success = true;
                foreach (TeamLineup team in teamList)
                {
                    if (team.Players.Count != playersPerTeam)
                    {
                        success = false;
                        break;
                    }
                }

                if (success)
                {
                    // Add participation records in the order that the players were queued.
                    pending.Sort((x, y) => DateTime.Compare(x.ValueRef.Timestamp, y.ValueRef.Timestamp));
                    foreach (LinkedListNode<QueuedPlayerOrGroup> node in pending)
                    {
                        ref QueuedPlayerOrGroup pog = ref node.ValueRef;
                        if (pog.Player is not null)
                        {
                            participantList.Add(pog.Player);
                        }
                        else if (pog.Group is not null)
                        {
                            foreach (Player player in pog.Group.Members)
                            {
                                participantList.Add(player);
                            }
                        }

                        s_nodePool.Return(node);
                    }

                    pending.Clear();

                    return true;
                }
                else
                {
                    // Unable to fill the teams.
                    // Add all the pending nodes back into the queue in their original order.
                    foreach (LinkedListNode<QueuedPlayerOrGroup> node in pending)
                    {
                        Add(node);
                    }
                    pending.Clear();

                    foreach (TeamLineup team in teamList)
                    {
                        team.Players.Clear();
                    }

                    return false;
                }
            }
            finally
            {
                Debug.Assert(pending.Count == 0);
                Debug.Assert(pendingSolo.Count == 0);

                s_listPool.Return(pending);
                s_listPool.Return(pendingSolo);
            }
        }
    }
}

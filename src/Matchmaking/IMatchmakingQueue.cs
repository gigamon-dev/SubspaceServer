using SS.Core;

namespace SS.Matchmaking
{
    /// <summary>
    /// Interface that a matchmaking queue should implement for it to be used with 
    /// the <see cref="Modules.MatchmakingQueues"/> module and associated <see cref="Interfaces.IMatchmakingQueues"/> interface.
    /// </summary>
    public interface IMatchmakingQueue
    {
        /// <summary>
        /// The name of the queue.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Options that define rules about the queue.
        /// </summary>
        QueueOptions Options { get; }

        /// <summary>
        /// A display friendly description about the queue.
        /// </summary>
        string Description { get; }

        #region Add

        /// <summary>
        /// Adds a solo player to the queue.
        /// </summary>
        /// <param name="player">The player to add.</param>
        /// <param name="timestamp">
        /// The timestamp the player should be added as of. Older timestamps have higher priority.
        /// Normally, this will be <see cref="DateTime.UtcNow"/>.
        /// For players that are requeuing from being a sub, 
        /// this value will be the original time the player queued, prior agreeing to sub.
        /// This way, players do not lose their spot in line if they sub in for an existing game.
        /// </param>
        /// <returns>True if the player was added. Otherwise, false.</returns>
        bool Add(Player player, DateTime timestamp);

        /// <summary>
        /// Adds a group to the queue.
        /// </summary>
        /// <param name="group">The group to add.</param>
        /// <returns>True if the group was added. Otherwise, false.</returns>
        bool Add(IPlayerGroup group, DateTime timestamp);

        #endregion

        #region Remove

        /// <summary>
        /// Removes a solo player from the queue.
        /// </summary>
        /// <param name="player">The player to remove.</param>
        /// <returns>True if the player was removed. Otherwise, false.</returns>
        bool Remove(Player player);

        /// <summary>
        /// Removes a group from the queue.
        /// </summary>
        /// <param name="group">The group to remove.</param>
        /// <returns>True if the group was removed. Otherwise, false.</returns>
        bool Remove(IPlayerGroup group);

        #endregion

        /// <summary>
        /// Gets the players and groups that are currently in the queue.
        /// </summary>
        /// <param name="soloPlayers">A set to populate with the solo players that are in the queue.</param>
        /// <param name="groups">A set to populate with the groups that are in the queue.</param>
        void GetQueued(HashSet<Player> soloPlayers, HashSet<IPlayerGroup> groups);

        #region Statistics

        /// <summary>
        /// The # of matches made.
        /// </summary>
        //int MatchesMade { get; }

        /// <summary>
        /// The # of players currently playing in a match.
        /// </summary>
        //int Playing { get; }

        /// <summary>
        /// The # of players queued.
        /// </summary>
        //int PlayersQueued { get; }

        /// <summary>
        /// The # of solo players currently queued.
        /// </summary>
        //int SoloPlayersQueued { get; }

        /// <summary>
        /// The # of groups queued.
        /// </summary>
        //int GroupsQueued { get; }

        #endregion
    }

    public struct QueueOptions
    {
        /// <summary>
        /// Whether the queue allows solo players to queue up.
        /// </summary>
        public bool AllowSolo { get; init; }

        /// <summary>
        /// Whether the queue allows groups to queue up.
        /// </summary>
        public bool AllowGroups { get; init; }

        /// <summary>
        /// The minimum # of players in a group to be able to queue up.
        /// </summary>
        /// <remarks>
        /// Must be 2 or greater. (e.g. a 4v4squad arena could have both <see cref="MinGroupSize"/> and <see cref="MaxGroupSize"/> set to 4)
        /// </remarks>
        public int? MinGroupSize { get; init; }

        /// <summary>
        /// The maximum # of players in a group to be able to queue up.
        /// </summary>
        public int? MaxGroupSize { get; init; }

        /// <summary>
        /// The minimum # of teams required for a successful match.
        /// </summary>
        /// <remarks>
        /// Must be >= 1.
        /// 1 could be used in a PvE scenario.
        /// </remarks>
        //public int MinTeams;

        /// <summary>
        /// The maximum # of teams allowed when matching.
        /// </summary>
        /// <remarks>
        /// null means do not enforce a maximum.
        /// </remarks>
        //public int? MaxTeams;

        /// <summary>
        /// The minimum # of players to fill a team.
        /// </summary>
        //public int MinPlayersPerTeam;

        /// <summary>
        /// The maximum # of players to fill a team.
        /// </summary>
        //public int MaxPlayersPerTeam;

        /// <summary>
        /// true means players in a group must be placed on the same team. false means players in a group can be split (but they get to play in the same match, even as opponents)
        /// </summary>
        //public bool GroupSameTeam;

        /// <summary>
        /// Whether to allow auto requeuing after a match has completed.
        /// </summary>
        public bool AllowAutoRequeue { get; init; }
    }
}

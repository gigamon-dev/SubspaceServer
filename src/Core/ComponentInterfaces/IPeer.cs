using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Interface for a service that provides connectivity with other zones.
    /// </summary>
    public interface IPeer : IComponentInterface
    {
        #region Types

        public interface IPeerZone
        {
            /// <summary>
            /// The configuration Id. E.g., [Peer3] is 3.
            /// </summary>
            int Id { get; }

            /// <summary>
            /// The IP address of the peer zone.
            /// </summary>
            /// <remarks>
            /// A peer zone is uniquely identified by its IP + port combination.
            /// <see cref="IPEndPoint"/> is mutable, therefore exposing the IP and port separately as unmutable properties.
            /// </remarks>
            ReadOnlySpan<byte> IPAddress { get; }

            /// <summary>
            /// The port of the peer zone.
            /// </summary>
            /// <remarks>
            /// A peer zone is uniquely identified by its IP + port combination.
            /// <see cref="IPEndPoint"/> is mutable, therefore exposing the IP and port separately as unmutable properties.
            /// </remarks>
            ushort Port { get; }

            /// <summary>
            /// If the peer is not sending a player list this will hold the content of the player count packet.
            /// Otherwise this value is always -1.
            /// </summary>
            int PlayerCount { get; }

            /// <summary>
            /// A list of arena names for which the server is to display a player count and redirect to the peer zone.
            /// </summary>
            /// <remarks>
            /// <para>This holds the values of PeerX:Arenas.</para>
            /// <para>This contains local names.</para>
            /// </remarks>
            IReadOnlyList<string> ConfiguredArenas { get; }

            /// <summary>
            /// A list of all the peer arenas received from the peer zone.
            /// </summary>
            /// <remarks>
            /// Note that it is possible for peer zones to not send a player list (configuration option),
            /// in which case this list is empty.  However, you can still receive a player count and messages.
            /// <para>
            /// It is better to read the count and use the indexer to access the list since
            /// enumerating over the collection will need to allocate to box the enumerator.
            /// </para>
            /// </remarks>
            IReadOnlyList<IPeerArena> Arenas { get; }
        }

        public interface IPeerArenaName
        {
            /// <summary>
            /// The name of the peer arena on this server.
            /// </summary>
            ReadOnlySpan<char> LocalName { get; }

            /// <summary>
            /// The name of the peer arena on the peer server.
            /// </summary>
            ReadOnlySpan<char> RemoteName { get; }

            /// <summary>
            /// Whether the difference between <see cref="LocalName"/> and <see cref="RemoteName"/> is only in the character casing.
            /// </summary>
            bool IsCaseChange { get; }
        }

        public interface IPeerArena
        {
            /// <summary>
            /// The arena Id, a random integer sent to us by the peer server.
            /// This value is unique per peer server, but not across peer servers.
            /// </summary>
            uint Id { get; }

            /// <summary>
            /// A locally generated Id, unique across all configured peer servers.
            /// This is used when relaying peer arenas.
            /// </summary>
            uint LocalId { get; }

            /// <summary>
            /// The name of the arena (local and remote).
            /// </summary>
            IPeerArenaName Name { get; }

            /// <summary>
            /// Whether the arena was specified in the config.
            /// </summary>
            public bool IsConfigured { get; }

            /// <summary>
            /// Whether the arena is set to be relayed to other peers.
            /// </summary>
            public bool IsRelay { get; }

            /// <summary>
            /// The timestamp when the last update was received.
            /// </summary>
            public DateTime LastUpdate { get; }

            /// <summary>
            /// The # of players in the arena.
            /// </summary>
            int PlayerCount { get; }
        }

        #endregion

        /// <summary>
        /// Returns the total population of all peer zones.
        /// </summary>
        /// <returns>The player count.</returns>
        int GetPopulationSummary();

        /// <summary>
        /// Find a playeyr in one of the peer zones.
        /// When a partial name is given, a best guess is returned.
        /// Guesses are based on a score, 0 means perfect match. 
        /// Higher values indicate how far from the start the given search string matches 
        /// (see <see cref="Modules.PlayerCommand.Command_find(string, string, Player, ITarget)"/>).
        /// Only arenas that the peer module has been configured for will be checked (PeerX:Arenas).
        /// </summary>
        /// <param name="findName">The player name to find.</param>
        /// <param name="score">The score this function has to beat, it is modified if a match is found.</param>
        /// <param name="name">A buffer to place the name of the found player in.</param>
        /// <param name="arena">A buffer to place the name of the arena of the player in (the local name).</param>
        /// <returns><see langword="true"/> if a match has been found.</returns>
        bool FindPlayer(ReadOnlySpan<char> findName, ref int score, StringBuilder name, StringBuilder arena);

        /// <summary>
        /// Attempts to place the given player in the arena of one of the peer zones.
        /// Only arenas that the peer module has been configured for will be handled (PeerX:Arenas).
        /// </summary>
        /// <param name="player">The player to place.</param>
        /// <param name="arenaType">The arena type the client sent us in the go packet.</param>
        /// <param name="arenaName">The arena name given in the go packet.</param>
        /// <returns><see langword="true"/> if the player was redirected to a peer zone.</returns>
        bool ArenaRequest(Player player, short arenaType, ReadOnlySpan<char> arenaName);

        #region SendZoneMessage

        /// <summary>
        /// Sends a zone chat message to all peer zones.
        /// </summary>
        /// <param name="handler">The message to send.</param>
        void SendZoneMessage([InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Sends a zone chat message to all peer zones.
        /// </summary>
        /// <param name="message">The message to send.</param>
        void SendZoneMessage(StringBuilder message);

        /// <summary>
        /// Sends a zone chat message to all peer zones.
        /// </summary>
        /// <param name="message">The message to send.</param>
        void SendZoneMessage(ReadOnlySpan<char> message);

        #endregion

        #region SendAlertMessage

        /// <summary>
        /// Sends an alert message to all connected staff in all the peer zones.
        /// </summary>
        /// <remarks>
        /// This is the same as ?help and ?cheater in subgame (Misc:AlertCommand).
        /// </remarks>
        /// <param name="alertName">The command that this alert was generated from. E.g., 'cheater'.</param>
        /// <param name="playerName">The name of the player that is sending the alert.</param>
        /// <param name="arenaName">The name of the arena that the player is in.</param>
        /// <param name="handler">The message to send.</param>
        void SendAlertMessage(ReadOnlySpan<char> alertName, ReadOnlySpan<char> playerName, ReadOnlySpan<char> arenaName, [InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Sends an alert message to all connected staff in all the peer zones.
        /// </summary>
        /// <remarks>
        /// This is the same as ?help and ?cheater in subgame (Misc:AlertCommand).
        /// </remarks>
        /// <param name="alertName">The command that this alert was generated from. E.g., 'cheater'.</param>
        /// <param name="playerName">The name of the player that is sending the alert.</param>
        /// <param name="arenaName">The name of the arena that the player is in.</param>
        /// <param name="message"></param>
        void SendAlertMessage(ReadOnlySpan<char> alertName, ReadOnlySpan<char> playerName, ReadOnlySpan<char> arenaName, StringBuilder message);

        /// <summary>
        /// Sends an alert message to all connected staff in all the peer zones.
        /// </summary>
        /// <remarks>
        /// This is the same as ?help and ?cheater in subgame (Misc:AlertCommand).
        /// </remarks>
        /// <param name="alertName">The command that this alert was generated from. E.g., 'cheater'.</param>
        /// <param name="playerName">The name of the player that is sending the alert.</param>
        /// <param name="arenaName">The name of the arena that the player is in.</param>
        /// <param name="message"></param>
        void SendAlertMessage(ReadOnlySpan<char> alertName, ReadOnlySpan<char> playerName, ReadOnlySpan<char> arenaName, ReadOnlySpan<char> message);

        #endregion

        void Lock();

        void Unlock();

        /// <summary>
        /// Find a peer zone using the given address and port.
        /// </summary>
        /// <remarks>
        /// Remember to use <see cref="Lock"/> and <see cref="Unlock"/>.
        /// </remarks>
        /// <param name="endpoint">IP address and port to match.</param>
        /// <returns>The first match, or <see langword="null"/> if not found.</returns>
        IPeerZone FindZone(IPEndPoint endpoint);

        /// <summary>
        /// Find a peer zone using the given address and port.
        /// </summary>
        /// <remarks>
        /// Remember to use <see cref="Lock"/> and <see cref="Unlock"/>.
        /// </remarks>
        /// <param name="address">IP address and port to match.</param>
        /// <returns>The first match, or <see langword="null"/> if not found.</returns>
        IPeerZone FindZone(SocketAddress address);

        /// <summary>
        /// Find a peer arena with the given <paramref name="arenaName"/>. 
        /// </summary>
        /// <remarks>
        /// <para>This may also return arenas that we are not configured to display to players for.</para>
        /// <para>Remember to use <see cref="Lock"/> and <see cref="Unlock"/>.</para>
        /// </remarks>
        /// <param name="arenaName">The name of the arena to match.</param>
        /// <param name="remote"><see langword="false"/> to match using the arena 'localName'. <see langword="true"/> to match using the arena 'remoteName'.</param>
        /// <returns>The first match, or <see langword="null"/> if not found.</returns>
        IPeerArena FindArena(ReadOnlySpan<char> arenaName, bool remote);

        /// <summary>
        /// This is a list of all the configured peer zones.
        /// </summary>
        /// <remarks>
        /// Remember to use <see cref="Lock"/> and <see cref="Unlock"/>.
        /// <para>
        /// It is better to read the count and use the indexer to access the list since
        /// enumerating over the collection will need to allocate to box the enumerator.
        /// </para>
        /// </remarks>
        IReadOnlyList<IPeerZone> Peers { get; }
    }
}

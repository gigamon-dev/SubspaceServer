using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Delegate for a handler to an incoming 'simple chat protocol' message.
    /// </summary>
    /// <param name="player">The player the mesage is from.</param>
    /// <param name="message">The message to process.</param>
    public delegate void ChatMessageHandler(Player player, ReadOnlySpan<char> message);

    /// <summary>
    /// Data about a single chat client connection.
    /// </summary>
    public struct ChatClientStats
    {
        /// <summary>
        /// The total # of bytes sent.
        /// </summary>
        public ulong BytesSent;

        /// <summary>
        /// The total # of bytes received.
        /// </summary>
        public ulong BytesReceived;
    }

    /// <summary>
    /// Interface for a service that provides functionality for the 'simple chat protocol'.
    /// </summary>
    public interface IChatNetwork : IComponentInterface
    {
        /// <summary>
        /// Adds a chat message handler.
        /// </summary>
        /// <param name="type">The type in the 'simple chat protocol' to add a handler for.</param>
        /// <param name="handler">The handler to add.</param>
        void AddHandler(ReadOnlySpan<char> type, ChatMessageHandler handler);

        //void CallHandler(Player player, ReadOnlySpan<char> type, ReadOnlySpan<char> line);

        /// <summary>
        /// Removes a chat message handler.
        /// </summary>
        /// <param name="type">The type in the 'simple chat protocol' to remove a handler for.</param>
        /// <param name="handler">The handler to remove.</param>
        void RemoveHandler(ReadOnlySpan<char> type, ChatMessageHandler handler);

        #region SendToOne

        /// <summary>
        /// Sends a message to a single player.
        /// </summary>
        /// <param name="player">The player to send the message to.</param>
        /// <param name="message">The message to send.</param>
        void SendToOne(Player player, ReadOnlySpan<char> message);

        /// <summary>
        /// Sends a message to a single player.
        /// </summary>
        /// <param name="player">The player to send the message to.</param>
        /// <param name="message">The message to send.</param>
        void SendToOne(Player player, StringBuilder message);

        /// <summary>
        /// Sends a message to a single player.
        /// </summary>
        /// <param name="player">The player to send the message to.</param>
        /// <param name="handler">The message to send.</param>
        void SendToOne(Player player, [InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Sends a message to a single player.
        /// </summary>
        /// <param name="player">The player to send the message to.</param>
        /// <param name="handler">The message to send.</param>
        void SendToOne(Player player, IFormatProvider provider, [InterpolatedStringHandlerArgument("", nameof(provider))] ref StringBuilderBackedInterpolatedStringHandler handler);

        #endregion

        #region SentToArena

        /// <summary>
        /// Sends a message to players in a specific arena, with the ability to exclude a specified player from the send.
        /// </summary>
        /// <param name="arena">The arena to send the message to.</param>
        /// <param name="except">The player to exclude from the send. <see langword="null"/> for no exclusion.</param>
        /// <param name="message">The message to send.</param>
        void SendToArena(Arena arena, Player? except, ReadOnlySpan<char> message);

        /// <summary>
        /// Sends a message to players in a specific arena, with the ability to exclude a specified player from the send.
        /// </summary>
        /// <param name="arena">The arena to send the message to.</param>
        /// <param name="except">The player to exclude from the send. <see langword="null"/> for no exclusion.</param>
        /// <param name="message">The message to send.</param>
        void SendToArena(Arena arena, Player? except, StringBuilder message);

        /// <summary>
        /// Sends a message to players in a specific arena, with the ability to exclude a specified player from the send.
        /// </summary>
        /// <param name="arena">The arena to send the message to.</param>
        /// <param name="except">The player to exclude from the send. <see langword="null"/> for no exclusion.</param>
        /// <param name="handler">The message to send.</param>
        void SendToArena(Arena arena, Player? except, [InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Sends a message to players in a specific arena, with the ability to exclude a specified player from the send.
        /// </summary>
        /// <param name="arena">The arena to send the message to.</param>
        /// <param name="except">The player to exclude from the send. <see langword="null"/> for no exclusion.</param>
        /// <param name="handler">The message to send.</param>
        void SendToArena(Arena arena, Player? except, IFormatProvider provider, [InterpolatedStringHandlerArgument("", nameof(provider))] ref StringBuilderBackedInterpolatedStringHandler handler);

        #endregion

        #region SendToSet

        /// <summary>
        /// Sends a message to a set of players.
        /// </summary>
        /// <param name="set">The players to send the message to.</param>
        /// <param name="message">The message to send.</param>
        void SendToSet(HashSet<Player> set, ReadOnlySpan<char> message);

        /// <summary>
        /// Sends a message to a set of players.
        /// </summary>
        /// <param name="set">The players to send the message to.</param>
        /// <param name="message">The message to send.</param>
        void SendToSet(HashSet<Player> set, StringBuilder message);

        /// <summary>
        /// Sends a message to a set of players.
        /// </summary>
        /// <param name="set">The players to send the message to.</param>
        /// <param name="handler">The message to send.</param>
        void SendToSet(HashSet<Player> set, [InterpolatedStringHandlerArgument("")] ref StringBuilderBackedInterpolatedStringHandler handler);

        /// <summary>
        /// Sends a message to a set of players.
        /// </summary>
        /// <param name="set">The players to send the message to.</param>
        /// <param name="handler">The message to send.</param>
        void SendToSet(HashSet<Player> set, IFormatProvider provider, [InterpolatedStringHandlerArgument("", nameof(provider))] ref StringBuilderBackedInterpolatedStringHandler handler);

        #endregion

        /// <summary>
        /// Gets information about a connection from a player that is connected with a chat client.
        /// </summary>
        /// <param name="player">The player to get information about.</param>
        /// <param name="ip">A buffer to populate with a textual representation of the player's IP address.</param>
        /// <param name="ipBytesWritten">The number of characters written to <paramref name="ip"/>.</param>
        /// <param name="port">The player's port.</param>
        /// <param name="stats">Information about the connection.</param>
        /// <returns></returns>
        bool TryGetClientStats(Player player, Span<char> ip, out int ipBytesWritten, out int port, out ChatClientStats stats);
    }
}

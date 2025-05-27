using SS.Core.ComponentInterfaces;
using System;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="ChatMessageDelegate"/> callback.
    /// </summary>
    [CallbackHelper]
    public static partial class ChatMessageCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a chat message is sent.
        /// </summary>
        /// <param name="arena">The arena the message is associated with. <see langword="null"/> if not associated with a specific arena.</param>
        /// <param name="player">The player that sent the message. <see langword="null"/> for messages not sent by a player.</param>
        /// <param name="type">The type of chat message.</param>
        /// <param name="sound">The sound to play along with the message.</param>
        /// <param name="toPlayer">The player that message was sent to. <see langword="null"/> for messages not sent to a specific player.</param>
        /// <param name="freq">The team the message was sent to. -1 for messages not sent to a specific team.</param>
        /// <param name="message">The message text that was sent.</param>
        public delegate void ChatMessageDelegate(Arena? arena, Player? player, ChatMessageType type, ChatSound sound, Player? toPlayer, short freq, ReadOnlySpan<char> message);
    }
}

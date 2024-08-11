using SS.Core.ComponentInterfaces;
using System;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="ChatMessageDelegate"/> callback.
    /// </summary>
    public static class ChatMessageCallback
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

        public static void Register(IComponentBroker broker, ChatMessageDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, ChatMessageDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena? arena, Player? player, ChatMessageType type, ChatSound sound, Player? toPlayer, short freq, ReadOnlySpan<char> message)
        {
            broker?.GetCallback<ChatMessageDelegate>()?.Invoke(arena, player, type, sound, toPlayer, freq, message);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, player, type, sound, toPlayer, freq, message);
        }
    }
}

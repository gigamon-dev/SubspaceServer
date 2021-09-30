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
        /// <param name="playerFrom"></param>
        /// <param name="type"></param>
        /// <param name="sound"></param>
        /// <param name="playerTo"></param>
        /// <param name="freq"></param>
        /// <param name="message"></param>
        public delegate void ChatMessageDelegate(Player playerFrom, ChatMessageType type, ChatSound sound, Player playerTo, short freq, ReadOnlySpan<char> message);

        public static void Register(ComponentBroker broker, ChatMessageDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, ChatMessageDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player playerFrom, ChatMessageType type, ChatSound sound, Player playerTo, short freq, ReadOnlySpan<char> message)
        {
            broker?.GetCallback<ChatMessageDelegate>()?.Invoke(playerFrom, type, sound, playerTo, freq, message);

            if (broker?.Parent != null)
                Fire(broker.Parent, playerFrom, type, sound, playerTo, freq, message);
        }
    }
}

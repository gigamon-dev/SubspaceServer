using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    public delegate void ChatMessageDelegate(Player playerFrom, ChatMessageType type, ChatSound sound, Player playerTo, short freq, string message);

    public static class ChatMessageCallback
    {
        public static void Register(ComponentBroker broker, ChatMessageDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, ChatMessageDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player playerFrom, ChatMessageType type, ChatSound sound, Player playerTo, short freq, string message)
        {
            broker?.GetCallback<ChatMessageDelegate>()?.Invoke(playerFrom, type, sound, playerTo, freq, message);

            if (broker?.Parent != null)
                Fire(broker.Parent, playerFrom, type, sound, playerTo, freq, message);
        }
    }
}

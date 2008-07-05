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
            broker.RegisterCallback<Player, ChatMessageType, ChatSound, Player, short, string>(Constants.Events.ChatMessage, new ComponentCallbackDelegate<Player, ChatMessageType, ChatSound, Player, short, string>(handler));
        }

        public static void Unregister(ComponentBroker broker, ChatMessageDelegate handler)
        {
            broker.RegisterCallback<Player, ChatMessageType, ChatSound, Player, short, string>(Constants.Events.ChatMessage, new ComponentCallbackDelegate<Player, ChatMessageType, ChatSound, Player, short, string>(handler));
        }

        public static void Fire(ComponentBroker broker, Player playerFrom, ChatMessageType type, ChatSound sound, Player playerTo, short freq, string message)
        {
            broker.DoCallback<Player, ChatMessageType, ChatSound, Player, short, string>(Constants.Events.ChatMessage, playerFrom, type, sound, playerTo, freq, message);
        }
    }
}

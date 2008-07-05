using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    public static class PlayerActionCallback
    {
        public delegate void PlayerActionDelegate(Player p, PlayerAction action, Arena arena);

        public static void Register(ComponentBroker broker, PlayerActionDelegate handler)
        {
            broker.RegisterCallback<Player, PlayerAction, Arena>(Constants.Events.PlayerAction, new ComponentCallbackDelegate<Player,PlayerAction,Arena>(handler));
        }

        public static void Unregister(ComponentBroker broker, PlayerActionDelegate handler)
        {
            broker.UnRegisterCallback<Player, PlayerAction, Arena>(Constants.Events.PlayerAction, new ComponentCallbackDelegate<Player, PlayerAction, Arena>(handler));
        }

        public static void Fire(ComponentBroker broker, Player p, PlayerAction action, Arena arena)
        {
            broker.DoCallback<Player, PlayerAction, Arena>(Constants.Events.PlayerAction, p, action, arena);
        }
    }
}

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
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PlayerActionDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player p, PlayerAction action, Arena arena)
        {
            broker?.GetCallback<PlayerActionDelegate>()?.Invoke(p, action, arena);

            if (broker?.Parent != null)
                Fire(broker.Parent, p, action, arena);
        }
    }
}

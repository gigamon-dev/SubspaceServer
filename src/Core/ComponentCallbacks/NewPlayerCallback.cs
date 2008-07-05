using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    public static class NewPlayerCallback
    {
        public delegate void NewPlayerDelegate(Player p, bool isNew);

        public static void Register(ComponentBroker broker, NewPlayerDelegate handler)
        {
            broker.RegisterCallback<Player, bool>(Constants.Events.NewPlayer, new ComponentCallbackDelegate<Player,bool>(handler));
        }

        public static void Unregister(ComponentBroker broker, NewPlayerDelegate handler)
        {
            broker.UnRegisterCallback<Player, bool>(Constants.Events.NewPlayer, new ComponentCallbackDelegate<Player,bool>(handler));
        }

        public static void Fire(ComponentBroker broker, Player p, bool isNew)
        {
            broker.DoCallback<Player, bool>(Constants.Events.NewPlayer, p, isNew);
        }
    }
}

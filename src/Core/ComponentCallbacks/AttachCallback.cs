using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    public static class AttachCallback
    {
        /// <summary>
        /// this callback is called whenever someone attaches or detaches.
        /// </summary>
        /// <param name="p">the player who is attaching or detaching</param>
        /// <param name="to">the player being attached to, or NULL when detaching</param>
        public delegate void AttachDelegate(Player p, Player to);

        public static void Register(ComponentBroker broker, AttachDelegate handler)
        {
            broker.RegisterCallback<Player, Player>(Constants.Events.Attach, new ComponentCallbackDelegate<Player, Player>(handler));
        }

        public static void Unregister(ComponentBroker broker, AttachDelegate handler)
        {
            broker.UnRegisterCallback<Player, Player>(Constants.Events.Attach, new ComponentCallbackDelegate<Player, Player>(handler));
        }

        public static void Fire(ComponentBroker broker, Player p, Player to)
        {
            broker.DoCallback<Player, Player>(Constants.Events.Attach, p, to);
        }
    }
}

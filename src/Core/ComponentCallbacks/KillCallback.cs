using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Core.Packets;

namespace SS.Core.ComponentCallbacks
{
    public static class KillCallback
    {
        public delegate void KillDelegate(Arena arena, Player killer, Player killed, short bty, short flagCount, short pts, Prize green);

        public static void Register(ComponentBroker broker, KillDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, KillDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, Player killer, Player killed, short bty, short flagCount, short pts, Prize green)
        {
            broker?.GetCallback<KillDelegate>()?.Invoke(arena, killer, killed, bty, flagCount, pts, green);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, killer, killed, bty, flagCount, pts, green);
        }
    }

    public static class PostKillCallback
    {
        public delegate void PostKillDelegate(Arena arena, Player killer, Player killed, short bty, short flagCount, short pts, Prize green);

        public static void Register(ComponentBroker broker, PostKillDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, PostKillDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Arena arena, Player killer, Player killed, short bty, short flagCount, short pts, Prize green)
        {
            broker?.GetCallback<PostKillDelegate>()?.Invoke(arena, killer, killed, bty, flagCount, pts, green);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena, killer, killed, bty, flagCount, pts, green);
        }
    }
}

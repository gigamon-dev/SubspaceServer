﻿using SS.Core.ComponentInterfaces;

namespace SS.Core.ComponentCallbacks
{
    public static class KothEndedCallback
    {
        /// <summary>
        /// Delegate for when a King of the Hill game has ended.
        /// </summary>
        /// <param name="arena">The arena the game was ended in.</param>
        public delegate void KothEndedDelegate(Arena arena);

        public static void Register(IComponentBroker broker, KothEndedDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(IComponentBroker broker, KothEndedDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(IComponentBroker broker, Arena arena)
        {
            broker?.GetCallback<KothEndedDelegate>()?.Invoke(arena);

            if (broker?.Parent != null)
                Fire(broker.Parent, arena);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="NewPlayerDelegate"/> callback.
    /// </summary>
    public static class NewPlayerCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> is allocated or deallocated.
        /// In general you probably want to use the <see cref="PlayerActionCallback.PlayerActionDelegate"/> 
        /// callback instead of this for general initialization tasks.
        /// </summary>
        /// <param name="p">The player being allocated or deallocated.</param>
        /// <param name="isNew">True if being allocated, false if being deallocated.</param>
        public delegate void NewPlayerDelegate(Player p, bool isNew);

        public static void Register(ComponentBroker broker, NewPlayerDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, NewPlayerDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player p, bool isNew)
        {
            broker?.GetCallback<NewPlayerDelegate>()?.Invoke(p, isNew);

            if (broker?.Parent != null)
                Fire(broker.Parent, p, isNew);
        }
    }
}

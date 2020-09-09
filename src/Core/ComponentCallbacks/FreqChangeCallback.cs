using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="FreqChangeDelegate"/> callback.
    /// </summary>
    public static class FreqChangeCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> changes their freq (team).
        /// </summary>
        /// <param name="p">The player that changed teams.</param>
        /// <param name="freq">The team the player changed to.</param>
        public delegate void FreqChangeDelegate(Player p, short freq);

        public static void Register(ComponentBroker broker, FreqChangeDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, FreqChangeDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player p, short freq)
        {
            broker?.GetCallback<FreqChangeDelegate>()?.Invoke(p, freq);

            if (broker?.Parent != null)
                Fire(broker.Parent, p, freq);
        }
    }
}

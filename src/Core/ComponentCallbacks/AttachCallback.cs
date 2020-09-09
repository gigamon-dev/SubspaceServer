using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="AttachDelegate"/> callback.
    /// </summary>
    public static class AttachCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a <see cref="Player"/> attaches or detaches.
        /// </summary>
        /// <param name="p">The player that is attaching or detaching.</param>
        /// <param name="to">The player being attached to, or <see langword="null"/> when detaching.</param>
        public delegate void AttachDelegate(Player p, Player to);

        public static void Register(ComponentBroker broker, AttachDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, AttachDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, Player p, Player to)
        {
            broker?.GetCallback<AttachDelegate>()?.Invoke(p, to);

            if (broker?.Parent != null)
                Fire(broker.Parent, p, to);
        }
    }
}

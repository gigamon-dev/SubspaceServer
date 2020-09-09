using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;

namespace SS.Core.ComponentCallbacks
{
    /// <summary>
    /// Helper class for the <see cref="ConnectionInitDelegate"/> callback.
    /// </summary>
    public static class ConnectionInitCallback
    {
        /// <summary>
        /// Delegate for a callback that is invoked when a connection is initialized.
        /// The intended use is for encryption modules to tie in.
        /// </summary>
        /// <param name="remoteEndpoint"></param>
        /// <param name="buffer"></param>
        /// <param name="len"></param>
        /// <param name="ld"></param>
        public delegate void ConnectionInitDelegate(IPEndPoint remoteEndpoint, byte[] buffer, int len, ListenData ld);

        public static void Register(ComponentBroker broker, ConnectionInitDelegate handler)
        {
            broker?.RegisterCallback(handler);
        }

        public static void Unregister(ComponentBroker broker, ConnectionInitDelegate handler)
        {
            broker?.UnregisterCallback(handler);
        }

        public static void Fire(ComponentBroker broker, IPEndPoint remoteEndpoint, byte[] buffer, int len, ListenData ld)
        {
            broker?.GetCallback<ConnectionInitDelegate>()?.Invoke(remoteEndpoint, buffer, len, ld);

            if (broker?.Parent != null)
                Fire(broker.Parent, remoteEndpoint, buffer, len, ld);
        }
    }
}

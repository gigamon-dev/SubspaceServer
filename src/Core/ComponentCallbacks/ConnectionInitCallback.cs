using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;

namespace SS.Core.ComponentCallbacks
{
    public delegate void ConnectionInitDelegate(IPEndPoint remoteEndpoint, byte[] buffer, int len, ListenData ld);

    public static class ConnectionInitCallback
    {
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

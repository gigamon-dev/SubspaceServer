using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;

namespace SS.Core.ComponentCallbacks
{
    public delegate void ConnectionInitDelegate(IPEndPoint remoteEndpoint, byte[] buffer, int len, object v);

    public static class ConnectionInitCallback
    {
        public static void Register(ComponentBroker broker, ConnectionInitDelegate handler)
        {
            broker.RegisterCallback<IPEndPoint, byte[], int, object>(Constants.Events.ConnectionInit, new ComponentCallbackDelegate<IPEndPoint, byte[], int, object>(handler));
        }

        public static void Unregister(ComponentBroker broker, ConnectionInitDelegate handler)
        {
            broker.UnRegisterCallback<IPEndPoint, byte[], int, object>(Constants.Events.ConnectionInit, new ComponentCallbackDelegate<IPEndPoint, byte[], int, object>(handler));
        }

        public static void Fire(ComponentBroker broker, IPEndPoint remoteEndpoint, byte[] buffer, int len, object v)
        {
            broker.DoCallback<IPEndPoint, byte[], int, object>(Constants.Events.ConnectionInit, remoteEndpoint, buffer, len, v);
        }
    }
}

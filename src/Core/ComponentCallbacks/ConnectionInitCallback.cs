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
            broker.RegisterCallback(Constants.Events.ConnectionInit, new ComponentCallbackDelegate<IPEndPoint, byte[], int, ListenData>(handler));
        }

        public static void Unregister(ComponentBroker broker, ConnectionInitDelegate handler)
        {
            broker.UnRegisterCallback(Constants.Events.ConnectionInit, new ComponentCallbackDelegate<IPEndPoint, byte[], int, ListenData>(handler));
        }

        public static void Fire(ComponentBroker broker, IPEndPoint remoteEndpoint, byte[] buffer, int len, ListenData ld)
        {
            broker.DoCallback(Constants.Events.ConnectionInit, remoteEndpoint, buffer, len, ld);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;

namespace SS.Core
{
    public class ListenData
    {
        public readonly Socket GameSocket;
        public readonly Socket PingSocket;
        
        /// <summary>
        /// used to determine the default arena users should be sent to
        /// </summary>
        public string ConnectAs;

        /// <summary>
        /// Whether VIE clients are allowed to connect
        /// </summary>
	    public bool AllowVIE;

        /// <summary>
        /// Whether Continuum clients are allowed to connect
        /// </summary>
        public bool AllowContinuum;

        /* dynamic population data */
	    //int total, playing;

        public ListenData(Socket gameSocket, Socket pingSocket)
        {
            GameSocket = gameSocket;
            PingSocket = pingSocket;
        }
    }
}

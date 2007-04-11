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
        
        // might need things (ASSS has it)
        //private string connectas;
	    //bool allowVIE;
        //bool allowContinuum;
   	    /* dynamic population data */
	    //int total, playing;

        public ListenData(Socket gameSocket, Socket pingSocket)
        {
            GameSocket = gameSocket;
            PingSocket = pingSocket;
        }
    }
}

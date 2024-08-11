using System;
using System.Net.Sockets;

namespace SS.Core
{
    /// <summary>
    /// Information about a pair of listening sockets.
    /// Typically, a "Zone" is represented by a single ListenData pair.
    /// However, the server can be set up listen on multiple ListenData pairs, 
    /// with each pair representing a particular "Arena" within the "Zone".
    /// 
    /// The <see cref="GameSocket"/> is the UDP socket used to send and receive game data over the "subspace" game protocol.
    /// 
    /// The <see cref="PingSocket"/> is the UDP socket used to check if a server is up and tell how many players there are.
    /// The port of the ping socket is the game socket + 1.
    /// </summary>
    public class ListenData
    {
        public readonly Socket GameSocket;
        public readonly Socket PingSocket;

        /// <summary>
        /// used to determine the default arena users should be sent to
        /// </summary>
        [ConfigHelp("Listen", "ConnectAs", ConfigScope.Global, typeof(string),
            Description = """
                This setting allows you to treat clients differently depending on which port they connect to. It serves as a
                virtual server identifier for the rest of the server. The standard arena placement module will use this as the name of
                a default arena to put clients who connect through this port in.
                """)]
        public string? ConnectAs { get; init; }

        /// <summary>
        /// Whether VIE clients are allowed to connect
        /// </summary>
        [ConfigHelp("Listen", "AllowVIE", ConfigScope.Global, typeof(bool), DefaultValue = "1",
            Description = "Whether VIE protocol clients (i.e., Subspace 1.34 and bots) are allowed to connect to this port.")]
        public bool AllowVIE { get; init; }

        /// <summary>
        /// Whether Continuum clients are allowed to connect
        /// </summary>
        [ConfigHelp("Listen", "AllowContinuum", ConfigScope.Global, typeof(bool), DefaultValue = "1",
            Description = "Whether Continuum clients are allowed to connect to this port.")]
        public bool AllowContinuum { get; init; }

        public ListenData(Socket gameSocket, Socket pingSocket)
        {
            GameSocket = gameSocket ?? throw new ArgumentNullException(nameof(gameSocket));
            PingSocket = pingSocket ?? throw new ArgumentNullException(nameof(pingSocket));
        }
    }
}

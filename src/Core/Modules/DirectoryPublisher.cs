using SS.Core.ComponentInterfaces;
using SS.Packets.Directory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for publishing the zone on directory servers.
    /// </summary>
    [CoreModuleInfo]
    public class DirectoryPublisher : IModule
    {
        private ComponentBroker _broker;
        private IArenaManager _arenaManager;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private INetwork _network;
        private IPlayerData _playerData;
        private IServerTimer _serverTimer;

        private readonly Dictionary<IPAddress, Socket> _socketDictionary = [];
        private readonly List<DirectoryListing> _listings = [];
        private readonly List<(IPEndPoint, SocketAddress)> _servers = [];
        private readonly object _lock = new();

        public bool Load(
            ComponentBroker broker,
            IArenaManager arenaManager,
            IConfigManager configManager,
            ILogManager logManager,
            INetwork network,
            IPlayerData playerData,
            IServerTimer serverTimer)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _serverTimer = serverTimer ?? throw new ArgumentNullException(nameof(serverTimer));

            Initialize();

            return true;
        }

        public bool Unload(ComponentBroker broker)
        {
            _serverTimer.ClearTimer(Timer_SendAnnouncements, null);

            lock (_lock)
            {
                _listings.Clear();

                foreach (Socket socket in _socketDictionary.Values)
                {
                    socket.Dispose();
                }

                _socketDictionary.Clear();
            }

            return true;
        }

        private void Initialize()
        {
            lock (_lock)
            {
                //
                // Listings
                //

                _listings.Clear();

                string password = _configManager.GetStr(_configManager.Global, "Directory", "Password");
                if (string.IsNullOrEmpty(password))
                    password = "cane";

                string defaultName = _configManager.GetStr(_configManager.Global, "Directory", "Name");
                string defaultDescription = _configManager.GetStr(_configManager.Global, "Directory", "Description");

                int index = 0;
                while (_network.TryGetListenData(index++, out IPEndPoint endPoint, out string connectAs))
                {
                    string name = null;
                    string description = null;

                    if (!string.IsNullOrWhiteSpace(connectAs))
                    {
                        string section = $"Directory-{connectAs}";

                        name = _configManager.GetStr(_configManager.Global, section, "Name");
                        description = _configManager.GetStr(_configManager.Global, section, "Description");
                    }

                    if (string.IsNullOrWhiteSpace(name))
                        name = defaultName;

                    if (string.IsNullOrWhiteSpace(description))
                        description = defaultDescription;

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        if (string.IsNullOrWhiteSpace(connectAs))
                            _logManager.LogM(LogLevel.Warn, nameof(DirectoryPublisher), $"No name for {endPoint}. It will not be published. Please set a name in [Directory].");
                        else
                            _logManager.LogM(LogLevel.Warn, nameof(DirectoryPublisher), $"No name for {endPoint}. It will not be published. Please set a name in [Directory] or [Directory-{connectAs}].");

                        continue;
                    }

                    Socket socket = GetOrCreateSocket(endPoint.Address);
                    if (socket == null)
                        continue;

                    _listings.Add(
                        new DirectoryListing()
                        {
                            Socket = socket,
                            ConnectAs = connectAs,
                            Packet = new S2D_Announcement()
                            {
                                IP = 0, // always zero, the directory server must look at the IP that sent the datagram (perhaps a way to prevent spoofing entries)
                                Port = (ushort)endPoint.Port,
                                Players = 0, // fill in later
                                Scorekeeping = 1, // always keep scores
                                Version = 134, // priit's updated dirserv requires this
                                Name = name,
                                Password = password,
                                Description = description,
                            },
                        });

                    _logManager.LogM(LogLevel.Info, nameof(DirectoryPublisher), $"Listen endpoint {endPoint} using name '{name}'.");
                }

                //
                // Directory servers
                //

                _servers.Clear();

                int defaultPort = _configManager.GetInt(_configManager.Global, "Directory", "Port", 4991);

                index = 0;
                while (++index > 0)
                {
                    string server = _configManager.GetStr(_configManager.Global, "Directory", $"Server{index}");
                    if (string.IsNullOrWhiteSpace(server))
                        break;

                    int port = _configManager.GetInt(_configManager.Global, "Directory", $"Port{index}", defaultPort);

                    IPHostEntry entry;
                    IPEndPoint directoryEndpoint = null;

                    try
                    {
                        entry = Dns.GetHostEntry(server);
                        IPAddress address = entry?.AddressList?.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                        if (address == null)
                        {
                            _logManager.LogM(LogLevel.Warn, nameof(DirectoryPublisher), $"Error looking up DNS entry of '{server}'. No IPv4 address found.");
                            continue;
                        }

                        directoryEndpoint = new IPEndPoint(address, port);
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogM(LogLevel.Warn, nameof(DirectoryPublisher), $"Error looking up DNS entry of '{server}'. {ex.Message}");
                        continue;
                    }

                    _servers.Add((directoryEndpoint, directoryEndpoint.Serialize()));

                    _logManager.LogM(LogLevel.Info, nameof(DirectoryPublisher), $"Using '{entry.HostName}' ({directoryEndpoint}) as a directory server.");
                }
            }

            //
            // Timer
            //

            _serverTimer.ClearTimer(Timer_SendAnnouncements, null);
            _serverTimer.SetTimer(Timer_SendAnnouncements, 10000, 60000, null);
        }

        private Socket GetOrCreateSocket(IPAddress bindAddress)
        {
            if (bindAddress == null)
                return null;

            if (_socketDictionary.TryGetValue(bindAddress, out Socket socket))
                return socket;

            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(DirectoryPublisher), $"Error creating socket. {ex.Message}");
                return null;
            }

            try
            {
                socket.Bind(new IPEndPoint(bindAddress, 0));
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(DirectoryPublisher), $"Error binding socket to {bindAddress} (any port). {ex.Message}");
                socket.Dispose();
                return null;
            }

            _socketDictionary.Add(bindAddress, socket);
            return socket;
        }

        private bool Timer_SendAnnouncements()
        {
            _logManager.LogM(LogLevel.Drivel, nameof(DirectoryPublisher), "Sending information to directory servers.");

            // Local arenas
            _arenaManager.GetPopulationSummary(out int globalTotal, out _);

            // Peer arenas
            IPeer peer = _broker.GetInterface<IPeer>();
            if (peer is not null)
            {
                try
                {
                    globalTotal += peer.GetPopulationSummary();
                }
                finally
                {
                    _broker.ReleaseInterface(ref peer);
                }
            }

            lock (_lock)
            {
                foreach (DirectoryListing listing in _listings)
                {
                    // update player counts
                    if (!string.IsNullOrWhiteSpace(listing.ConnectAs)
                        && _network.TryGetPopulationStats(listing.ConnectAs, out uint total, out _))
                    {
                        listing.Packet.Players = (ushort)total;
                    }
                    else
                    {
                        listing.Packet.Players = (ushort)globalTotal;
                    }

                    int length = listing.Packet.Length;
                    Span<byte> data = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref listing.Packet, 1))[..length];

                    // send
                    foreach ((IPEndPoint endPoint, SocketAddress socketAddress) in _servers)
                    {
                        try
                        {
                            listing.Socket.SendTo(data, SocketFlags.None, socketAddress);
                        }
                        catch (SocketException ex)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(DirectoryPublisher), $"SocketException with error code {ex.ErrorCode} when sending to {endPoint} with socket {listing.Socket.LocalEndPoint}. {ex}");
                        }
                        catch (Exception ex)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(DirectoryPublisher), $"Exception when sending to {endPoint} with socket {listing.Socket.LocalEndPoint}. {ex}");
                        }
                    }
                }
            }

            return true;
        }

        private class DirectoryListing
        {
            public Socket Socket { get; init; }
            public string ConnectAs { get; init; }
            public S2D_Announcement Packet;
        }
    }
}

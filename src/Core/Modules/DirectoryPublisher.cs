using SS.Core.ComponentInterfaces;
using SS.Packets.Directory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module for publishing the zone on directory servers.
    /// </summary>
    /// <remarks>
    /// As noted in the ASSS comments:
    /// <code>
    /// information on the directory server protocol was obtained from
    /// Hammuravi's page at
    /// http://www4.ncsu.edu/~rniyenga/subspace/old/dprotocol.html
    /// </code>
    /// It can be read on the Internet Archive's Wayback Machine. 
    /// https://web.archive.org/web/20041208173200/http://www4.ncsu.edu/~rniyenga/subspace/old/dprotocol.html
    /// </remarks>
    [CoreModuleInfo]
    public sealed class DirectoryPublisher : IAsyncModule, IDisposable
    {
        private readonly IComponentBroker _broker;
        private readonly IArenaManager _arenaManager;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly INetwork _network;
        private readonly IPlayerData _playerData;
        private readonly IServerTimer _serverTimer;

        private readonly Dictionary<IPAddress, Socket> _socketDictionary = [];
        private readonly List<DirectoryListing> _listings = [];
        private readonly List<(IPEndPoint, SocketAddress)> _servers = [];
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private bool _isDisposed;

        public DirectoryPublisher(
            IComponentBroker broker,
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
        }

        #region Module members

        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            await InitializeAsync().ConfigureAwait(false);
            return true;
        }

        Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            _serverTimer.ClearTimer(Timer_SendAnnouncements, null);

            _semaphore.Wait(cancellationToken);

            try
            {
                _listings.Clear();

                foreach (Socket socket in _socketDictionary.Values)
                {
                    socket.Dispose();
                }

                _socketDictionary.Clear();
            }
            finally
            {
                _semaphore.Release();
            }

            return Task.FromResult(true);
        }

        #endregion

        #region IDisposable

        private void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _semaphore.Dispose();
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion

        [ConfigHelp("Directory", "Password", ConfigScope.Global, typeof(string), DefaultValue = "cane",
            Description = "The password used to send information to the directory server. Don't change this.")]
        [ConfigHelp("Directory", "Name", ConfigScope.Global, typeof(string),
            Description = """
                The server name to send to the directory server. Virtual
                servers will use section name 'Directory-<vs-name>' for this
                and other settings in this section, and will fall back to
                'Directory' if that section doesn't exist. See Net:Listen
                help for how to identify virtual servers.
                """)]
        [ConfigHelp("Directory", "Description", ConfigScope.Global, typeof(string),
            Description = """
                The server description to send to the directory server. See
                Directory:Name for more information about the section name.
                """)]
        [ConfigHelp("Directory", "Port", ConfigScope.Global, typeof(int), DefaultValue = "4991",
            Description = "The port to connect to for the directory server.")]
        [ConfigHelp("Directory", "ServerN", ConfigScope.Global, typeof(int),
            Description = "The DNS name to connect to for the Nth directory server.")]
        [ConfigHelp("Directory", "PortN", ConfigScope.Global, typeof(int),
            Description = """
                The port to connect to for the Nth directory server.
                If no port is specified, Directory:Port is used.
                """)]
        private async Task InitializeAsync()
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                //
                // Listings
                //

                _listings.Clear();

                string? password = _configManager.GetStr(_configManager.Global, "Directory", "Password");
                if (string.IsNullOrEmpty(password))
                    password = "cane";

                string? defaultName = _configManager.GetStr(_configManager.Global, "Directory", "Name");
                string? defaultDescription = _configManager.GetStr(_configManager.Global, "Directory", "Description");

                int index = 0;
                while (_network.TryGetListenData(index++, out IPEndPoint? endPoint, out string? connectAs))
                {
                    string? name = null;
                    string? description = null;

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

                    Socket? socket = GetOrCreateSocket(endPoint.Address);
                    if (socket is null)
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

                
                for (index = 1; index <= 99; index++)
                {
                    string? server = GetServerSetting(index);
                    if (string.IsNullOrWhiteSpace(server))
                        break;

                    int port = GetPortSetting(index, defaultPort);

                    IPHostEntry entry;
                    IPEndPoint? directoryEndpoint = null;

                    try
                    {
                        entry = await Dns.GetHostEntryAsync(server).ConfigureAwait(false);
                        IPAddress? address = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

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
            finally
            {
                _semaphore.Release();
            }

            //
            // Timer
            //

            _serverTimer.ClearTimer(Timer_SendAnnouncements, null);
            _serverTimer.SetTimer(Timer_SendAnnouncements, 10000, 60000, null);

            string? GetServerSetting(int index)
            {
                Span<char> keySpan = stackalloc char["Server".Length + 11];
                if (!keySpan.TryWrite($"Server{index}", out int charsWritten))
                    return null;

                return _configManager.GetStr(_configManager.Global, "Directory", keySpan[..charsWritten]);
            }

            int GetPortSetting(int index, int defaultPort)
            {
                Span<char> keySpan = stackalloc char["Port".Length + 11];
                if (!keySpan.TryWrite($"Port{index}", out int charsWritten))
                    return defaultPort;

                return _configManager.GetInt(_configManager.Global, "Directory", keySpan[..charsWritten], defaultPort);
            }
        }

        private Socket? GetOrCreateSocket(IPAddress bindAddress)
        {
            if (bindAddress is null)
                return null;

            if (_socketDictionary.TryGetValue(bindAddress, out Socket? socket))
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
            IPeer? peer = _broker.GetInterface<IPeer>();
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

            _semaphore.Wait();

            try
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
            finally
            {
                _semaphore.Release();
            }

            return true;
        }

        private class DirectoryListing
        {
            public required Socket Socket { get; init; }
            public required string? ConnectAs { get; init; }
            public required S2D_Announcement Packet;
        }
    }
}

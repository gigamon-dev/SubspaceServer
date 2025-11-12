using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Packets;
using SS.Packets.Game;
using SS.Packets.Peer;
using SS.Utilities;
using SS.Utilities.Collections;
using SS.Utilities.ObjectPool;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ListenSettings = SS.Core.ConfigHelp.Constants.Global.Listen;
using NetSettings = SS.Core.ConfigHelp.Constants.Global.Net;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality to communicate using UDP and the Subspace 'core' protocol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Though primarily designed towards being used as a server, this module also provides functionality for use as a client.
    /// The <see cref="INetwork"/> interface provides methods for server functionality,
    /// and the <see cref="INetworkClient"/> interface provides functionality for being a client.
    /// As a 'zone' server it acts as a server that listens for 'game' clients to connect.
    /// However, it also acts as a client to other servers, such as a 'billing' server (<see cref="BillingUdp"/>).
    /// </para>
    /// <para>
    /// This module is equivalent to the 'net' module in ASSS. However, in many ways it is enhanced and implemented differently.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public sealed class Network : IModule, IModuleLoaderAware, INetwork, INetworkClient, IRawNetwork, IDisposable
    {
        private readonly IComponentBroker _broker;
        private readonly IBandwidthLimiterProvider _bandwidthLimiterProvider;
        private readonly IConfigManager _configManager;
        private readonly ILagCollect _lagCollect;
        private readonly ILogManager _logManager;
        private readonly IMainloop _mainloop;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;
        private readonly IPrng _prng;
        private InterfaceRegistrationToken<INetwork>? _iNetworkToken;
        private InterfaceRegistrationToken<INetworkClient>? _iNetworkClientToken;
        private InterfaceRegistrationToken<IRawNetwork>? _iRawNetworkToken;

        private readonly Pool<SubspaceBuffer> _bufferPool;
        private readonly Pool<BigReceive> _bigReceivePool;
        private readonly Pool<ReliablePlayerCallbackInvoker> _reliablePlayerCallbackInvokerPool;
        private readonly Pool<ReliableClientConnectionCallbackInvoker> _reliableClientConnectionCallbackInvokerPool;
        private readonly Pool<ReliableConnectionCallbackInvoker> _reliableConnectionCallbackInvokerPool;
        private readonly ObjectPool<LinkedListNode<SubspaceBuffer>> _bufferNodePool;
        private readonly DefaultObjectPool<LinkedListNode<ISizedSendData>> _sizedSendDataNodePool = new(new LinkedListNodePooledObjectPolicy<ISizedSendData>(), Constants.TargetPlayerCount);
        private static readonly DefaultObjectPool<LinkedListNode<ConnData>> s_connDataNodePool = new(new LinkedListNodePooledObjectPolicy<ConnData>(), Constants.TargetPlayerCount * 2);
        private readonly DefaultObjectPool<ClientConnection> _clientConnectionPool;

        /// <summary>
        /// Config settings.
        /// </summary>
        private readonly Config _config = new();

        /// <summary>
        /// Data used to respond to pings.
        /// </summary>
        private readonly PingData _pingData = new();

        /// <summary>
        /// Global network statistics.
        /// </summary>
        private readonly NetStats _globalStats = new();

        /// <summary>
        /// per player data key to ConnData
        /// </summary>
        private PlayerDataKey<PlayerConnection> _connKey;

        /// <summary>
        /// Dictionary of known player connections.
        /// </summary>
        /// <remarks>
        /// Key = SocketAddress (buffer containing IP, port, and address family)
        /// Value = Player
        /// </remarks>
        private readonly ConcurrentDictionary<SocketAddress, Player> _playerConnections = new();

        /// <summary>
        /// Dictionary of active client connections.
        /// </summary>
        /// <remarks>
        /// Synchronized with <see cref="_clientConnectionsLock"/>.
        /// </remarks>
        private readonly Dictionary<SocketAddress, ClientConnection> _clientConnections = [];
        private readonly ReaderWriterLockSlim _clientConnectionsLock = new(LockRecursionPolicy.SupportsRecursion);

        private delegate void CorePacketHandler(Span<byte> data, ConnData conn, NetReceiveFlags flags);

        /// <summary>
        /// Handlers for 'core' packets (ss protocol's network/transport layer).
        /// </summary>
        /// <remarks>
        /// The first byte of these packets is 0x00.
        /// The second byte identifies the type and is the index into this array.
        /// </remarks>
        private readonly CorePacketHandler?[] _oohandlers;

        /// <summary>
        /// The maximum # of packet types to allow.
        /// </summary>
        private const int MaxPacketTypes = 64;

        /// <summary>
        /// Handlers for 'game' packets that are received.
        /// </summary>
        private readonly PacketHandler?[] _handlers = new PacketHandler[MaxPacketTypes];

        /// <summary>
        /// Handlers for special network layer AKA 'core' packets that are received.
        /// </summary>
        private readonly PacketHandler?[] _nethandlers = new PacketHandler[0x14];

        /// <summary>
        /// Handlers for sized packets (0x0A) that are received.
        /// </summary>
        private readonly SizedPacketHandler?[] _sizedhandlers = new SizedPacketHandler[MaxPacketTypes];

        /// <summary>
        /// Handlers for connection init packets that are received.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Connection init packets include: 0x00 0x01 and 0x00 0x11.
        /// Peer packets are also included since they also begin with 0x00 0x01 (though they are a separate protocol).
        /// </para>
        /// <para>
        /// Synchronized with <see cref="_connectionInitLock"/>.
        /// </para>
        /// </remarks>
        private readonly List<ConnectionInitHandler> _connectionInitHandlers = [];
        private readonly ReaderWriterLockSlim _connectionInitLock = new(LockRecursionPolicy.NoRecursion);

        private PeerPacketHandler? _peerPacketHandler;

        private const int MICROSECONDS_PER_MILLISECOND = 1000;

        /// <summary>
        /// Queue for processing incoming reliable data.
        /// </summary>
        /// <remarks>
        /// When reliable data is received from known connection, that connection's <see cref="ConnData"/> 
        /// is added to this queue so that the reliable data can be processed by a <see cref="RelThread"/>.
        /// </remarks>
        private readonly HybridEventQueue<ConnData> _relQueue = new(Constants.TargetPlayerCount, s_connDataNodePool);

        /// <summary>
        /// Queue for sending sized data.
        /// </summary>
        private readonly HybridEventQueue<ConnData> _sizedSendQueue = new(Constants.TargetPlayerCount, s_connDataNodePool);

        // Used to stop the SendThread, ReceiveThread, RelThread, and SizedSendThread.
        private readonly CancellationTokenSource _stopCancellationTokenSource = new();
        private CancellationToken _stopToken;

        private readonly List<Thread> _threadList = new(8);

        /// <summary>
        /// List of info about the sockets being listened on.
        /// </summary>
        private readonly List<ListenData> _listenDataList = new(8);
        private readonly ReadOnlyCollection<ListenData> _readOnlyListenData;

        /// <summary>
        /// The socket for <see cref="ClientConnection"/>s.
        /// That is, for when this server is a client of another (e.g., to a billing server).
        /// </summary>
        private Socket? _clientSocket;

        /// <summary>
        /// A buffer to be used only by the <see cref="ReceiveThread"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Using the pinned object heap to be more efficient when working with sockets (native I/O).
        /// </para>
        /// <para>
        /// We need to receive the max size since <see cref="Socket.ReceiveFrom"/> will throw a <see cref="SocketException"/> (10040) if the datagram was larger than the buffer.
        /// Otherwise, a malicious actor could send us large packets to force exceptions to be thrown, slowing us down.
        /// The max size of a UDP packet is 65,507 bytes (IPv4) and 65,527 (IPv6). So, going with the larger size.
        /// </para>
        /// </remarks>
        private readonly byte[] _receiveBuffer = GC.AllocateArray<byte>(length: 65527, pinned: true);

        /// <summary>
        /// A reusable IPv4 <see cref="SocketAddress"/> to be used only by the <see cref="ReceiveThread"/>.
        /// </summary>
        private readonly SocketAddress _receiveSocketAddressV4 = new(AddressFamily.InterNetwork);

        /// <summary>
        /// A reusable IPv6 <see cref="SocketAddress"/> to be used only by the <see cref="ReceiveThread"/>.
        /// </summary>
        private readonly SocketAddress _receiveSocketAddressV6 = new(AddressFamily.InterNetworkV6);

        // TODO: Use a pinned array for sending too?
        // Socket sends can be done by:
        // - the send thread
        // - the receive thread (it also responds for core packets: sending ACKs, time sync response, responses from connection init handlers, etc...).
        // - the reliable thread(s) (it also responds for core packets)
        // Probably need a pool? Get one large byte[] and split it into multiple Memory<byte> of 512 bytes? Would need 2 + # of reliable threads
        //private readonly byte[] _receiveBuffer = GC.AllocateArray<byte>(length: 512*(2+reliableThreadCount), pinned: true);
        //private readonly ConcurrentBag<Memory<byte>> _sendBufferPool = 

        // Cached delegates
        private readonly Action<BigPacketWork> _mainloopWork_CallBigPacketHandlers;
        private readonly Action<SubspaceBuffer> _mainloopWork_CallPacketHandlers;
        private readonly ReliableConnectionCallbackInvoker.ReliableConnectionCallback _sizedSendChunkCompleted;
        private readonly Func<bool> _isCancellationRequested;
        private readonly Action<ClientConnection> _mainloopWork_ClientConnectionHandlerDisconnected;

        public Network(
            IComponentBroker broker,
            IBandwidthLimiterProvider bandwidthLimiterProvider,
            IConfigManager configManager,
            ILagCollect lagCollect,
            ILogManager logManager,
            IMainloop mainloop,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IPrng prng)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _bandwidthLimiterProvider = bandwidthLimiterProvider ?? throw new ArgumentNullException(nameof(bandwidthLimiterProvider));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _lagCollect = lagCollect ?? throw new ArgumentNullException(nameof(lagCollect));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));

            _config.Load(_configManager);

            _bufferPool = _objectPoolManager.GetPool<SubspaceBuffer>();
            _bigReceivePool = _objectPoolManager.GetPool<BigReceive>();
            _reliablePlayerCallbackInvokerPool = _objectPoolManager.GetPool<ReliablePlayerCallbackInvoker>();
            _reliableClientConnectionCallbackInvokerPool = _objectPoolManager.GetPool<ReliableClientConnectionCallbackInvoker>();
            _reliableConnectionCallbackInvokerPool = _objectPoolManager.GetPool<ReliableConnectionCallbackInvoker>();
            _bufferNodePool = new DefaultObjectPool<LinkedListNode<SubspaceBuffer>>(new LinkedListNodePooledObjectPolicy<SubspaceBuffer>(), Constants.TargetPlayerCount * _config.MaxOutlistSize);
            _clientConnectionPool = new DefaultObjectPool<ClientConnection>(new ClientConnectionPooledObjectPolicy(_config.ClientConnectionReliableReceiveWindowSize), Constants.TargetClientConnectionCount);

            // Allocate callback delegates once rather than each time they're used.
            _mainloopWork_CallBigPacketHandlers = MainloopWork_CallBigPacketHandlers;
            _mainloopWork_CallPacketHandlers = MainloopWork_CallPacketHandlers;
            _sizedSendChunkCompleted = SizedSendChunkCompleted;
            _isCancellationRequested = IsCancellationRequested;
            _mainloopWork_ClientConnectionHandlerDisconnected = MainloopWork_ClientConnectionHandlerDisconnected;

            _oohandlers = new CorePacketHandler[20];

            _oohandlers[0]  = null;                      // 0x00 - nothing
            _oohandlers[1]  = null;                      // 0x01 - key initiation
            _oohandlers[2]  = CorePacket_KeyResponse;    // 0x02 - key response
            _oohandlers[3]  = CorePacket_Reliable;       // 0x03 - reliable
            _oohandlers[4]  = CorePacket_Ack;            // 0x04 - reliable response
            _oohandlers[5]  = CorePacket_SyncRequest;    // 0x05 - time sync request
            _oohandlers[6]  = null;                      // 0x06 - time sync response
            _oohandlers[7]  = CorePacket_Drop;           // 0x07 - close connection
            _oohandlers[8]  = CorePacket_BigData;        // 0x08 - bigpacket
            _oohandlers[9]  = CorePacket_BigData;        // 0x09 - bigpacket end
            _oohandlers[10] = CorePacket_SizedData;      // 0x0A - sized data transfer (incoming)
            _oohandlers[11] = CorePacket_CancelSized;    // 0x0B - request to cancel the current outgoing sized data transfer
            _oohandlers[12] = CorePacket_SizedCancelled; // 0x0C - incoming sized data transfer has been cancelled
            _oohandlers[13] = null;                      // 0x0D - nothing
            _oohandlers[14] = CorePacket_Grouped;        // 0x0E - grouped
            _oohandlers[15] = null;                      // 0x0F
            _oohandlers[16] = null;                      // 0x10
            _oohandlers[17] = null;                      // 0x11
            _oohandlers[18] = null;                      // 0x12
            _oohandlers[19] = CorePacket_Special;        // 0x13 - cont key response

            _readOnlyListenData = _listenDataList.AsReadOnly();
        }

        #region Module Members

        bool IModule.Load(IComponentBroker broker)
        {
            _connKey = _playerData.AllocatePlayerData(new PlayerConnectionPooledObjectPolicy(_config.PlayerReliableReceiveWindowSize));

            if (!InitializeSockets())
                return false;

            _stopToken = _stopCancellationTokenSource.Token;

            // receive thread
            Thread thread = new(ReceiveThread) { Name = $"{nameof(Network)}-recv" };
            thread.Start();
            _threadList.Add(thread);

            // send thread
            thread = new Thread(SendThread) { Name = $"{nameof(Network)}-send" };
            thread.Start();
            _threadList.Add(thread);

            // reliable threads
            int reliableThreadCount = _configManager.GetInt(_configManager.Global, "Net", "ReliableThreads", 1);
            if (reliableThreadCount < 1)
                reliableThreadCount = 1;

            for (int i = 0; i < reliableThreadCount; i++)
            {
                thread = new Thread(RelThread) { Name = $"{nameof(Network)}-rel-" + i };
                thread.Start();
                _threadList.Add(thread);
            }

            // sized send thread
            thread = new(SizedSendThread) { Name = $"{nameof(Network)}-sized-send" };
            thread.Start();
            _threadList.Add(thread);

            _iNetworkToken = broker.RegisterInterface<INetwork>(this);
            _iNetworkClientToken = broker.RegisterInterface<INetworkClient>(this);
            _iRawNetworkToken = broker.RegisterInterface<IRawNetwork>(this);
            return true;


            [ConfigHelp<int>("Net", "InternalClientPort", ConfigScope.Global, Default = 0,
                Description = "The bind port for the internal client socket (used to communicate with billing servers).")]
            bool InitializeSockets()
            {
                //
                // Listen sockets (pairs of game and ping sockets)
                //

                int x = 0;
                ListenData? listenData;

                while ((listenData = CreateListenDataSockets(x++)) is not null)
                {
                    _listenDataList.Add(listenData);

                    if (!string.IsNullOrWhiteSpace(listenData.ConnectAs))
                    {
                        if (!_pingData.ConnectAsPopulationStats.ContainsKey(listenData.ConnectAs))
                        {
                            _pingData.ConnectAsPopulationStats.Add(listenData.ConnectAs, new PopulationStats());
                        }
                    }

                    _logManager.LogM(LogLevel.Drivel, nameof(Network), $"Listening on {listenData.GameSocket.LocalEndPoint}.");
                }

                //
                // Client socket (for communicating with billing servers)
                //

                int bindPort = _configManager.GetInt(_configManager.Global, "Net", "InternalClientPort", NetSettings.InternalClientPort.Default);

                try
                {
                    _clientSocket = CreateSocket(bindPort, IPAddress.Any);
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"Unable to create socket for client connections. {ex.Message}");
                }

                return true;

                [ConfigHelp<int>("Listen", "Port", ConfigScope.Global,
                    Description = """
                        The port that the game protocol listens on. Sections named
                        Listen1, Listen2, ... are also supported. All Listen
                        sections must contain a port setting.
                        """)]
                [ConfigHelp("Listen", "BindAddress", ConfigScope.Global,
                    Description = """
                        The interface address to bind to. This is optional, and if
                        omitted, the server will listen on all available interfaces.
                        """)]
                [ConfigHelp("Listen", "ConnectAs", ConfigScope.Global,
                    Description = """
                        This setting allows you to treat clients differently
                        depending on which port they connect to. It serves as a
                        virtual server identifier for the rest of the server.The
                        standard arena placement module will use this as the name of
                        a default arena to put clients who connect through this port
                        in.
                        """)]
                [ConfigHelp<bool>("Listen", "AllowVIE", ConfigScope.Global, Default = true,
                    Description = "Whether VIE protocol clients (i.e., Subspace 1.34 and bots) are allowed to connect to this port.")]
                [ConfigHelp<bool>("Listen", "AllowCont", ConfigScope.Global, Default = true,
                    Description = "Whether Continuum clients are allowed to connect to this port.")]
                ListenData? CreateListenDataSockets(int configIndex)
                {
                    string configSection = (configIndex == 0) ? "Listen" : $"Listen{configIndex}";

                    int gamePort = _configManager.GetInt(_configManager.Global, configSection, "Port", -1);
                    if (gamePort == -1)
                        return null;

                    string? bindAddressStr = _configManager.GetStr(_configManager.Global, configSection, "BindAddress");
                    IPAddress bindAddress = IPAddress.Any;
                    if (string.IsNullOrEmpty(bindAddressStr) == false)
                    {
                        try
                        {
                            IPAddress[] addresses = Dns.GetHostAddresses(bindAddressStr);
                            if (addresses.Length > 0)
                                bindAddress = addresses[0];
                        }
                        catch
                        {
                            // ignore and just stick with IPAddress.Any
                        }
                    }

                    int pingPort = gamePort + 1;

                    Socket gameSocket;
                    Socket pingSocket;

                    try
                    {
                        gameSocket = CreateSocket(gamePort, bindAddress);
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(Network), $"Unable to create game socket. {ex.Message}");
                        return null;
                    }

                    try
                    {
                        pingSocket = CreateSocket(pingPort, bindAddress);
                    }
                    catch (Exception ex)
                    {
                        gameSocket.Close();
                        _logManager.LogM(LogLevel.Error, nameof(Network), $"Unable to create ping socket: {ex.Message}");
                        return null;
                    }

                    ListenData listenData = new(gameSocket, pingSocket)
                    {
                        ConnectAs = _configManager.GetStr(_configManager.Global, configSection, "ConnectAs"),
                        AllowVIE = _configManager.GetBool(_configManager.Global, configSection, "AllowVIE", ListenSettings.AllowVIE.Default),
                        AllowContinuum = _configManager.GetBool(_configManager.Global, configSection, "AllowCont", ListenSettings.AllowCont.Default),
                    };

                    return listenData;
                }

                Socket CreateSocket(int port, IPAddress bindAddress)
                {
                    Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        // For Windows (Winsock) only,
                        // to prevent the exception "An existing connection was forcibly closed by the remote host"
                        // http://support.microsoft.com/kb/263823
                        const int SIO_UDP_CONNRESET = -1744830452;  // since IOControl() takes int instead of uint
                        byte[] optionInValue = [0, 0, 0, 0];
                        socket.IOControl(SIO_UDP_CONNRESET, optionInValue, null);
                    }

                    try
                    {
                        socket.Blocking = false;
                    }
                    catch (Exception ex)
                    {
                        // not fatal, just warn
                        _logManager.LogM(LogLevel.Warn, nameof(Network), $"Can't make socket nonblocking. {ex.Message}");
                    }

                    bindAddress ??= IPAddress.Any;

                    try
                    {
                        socket.Bind(new IPEndPoint(bindAddress, port));
                    }
                    catch (Exception ex)
                    {
                        socket.Close();
                        throw new Exception($"Cannot bind socket to {bindAddress}:{port}.", ex);
                    }

                    return socket;
                }
            }
        }

        void IModuleLoaderAware.PostLoad(IComponentBroker broker)
        {
            // NOOP
        }

        void IModuleLoaderAware.PreUnload(IComponentBroker broker)
        {
            Debug.Assert(_mainloop.IsMainloop);

            // All connections need to be disconnected in PreUnload because they are holding onto interfaces of encryption modules.
            // The encryption modules will be unable to Unload if the interfaces are not released first.
            StopThreadsAndDisconnectConnections();
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iNetworkToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iNetworkClientToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iRawNetworkToken) != 0)
                return false;

            // Normally, all threads are already stopped and all connections disconnected by this point because PreUnload should have ran.
            // However, if there's a failure to load all modules during startup, then the PostLoad/PreUnload steps will NOT be run.
            // So, stopping threads and disconnecting connections needs to be done here too.
            StopThreadsAndDisconnectConnections();

            Array.Clear(_handlers);
            Array.Clear(_nethandlers);
            Array.Clear(_sizedhandlers);

            _relQueue.Clear();
            _sizedSendQueue.Clear();

            // close all sockets
            foreach (ListenData listenData in _listenDataList)
            {
                listenData.GameSocket.Dispose();
                listenData.PingSocket.Dispose();
            }
            _listenDataList.Clear();

            _pingData.ConnectAsPopulationStats.Clear();

            if (_clientSocket is not null)
            {
                _clientSocket.Dispose();
                _clientSocket = null;
            }

            _playerData.FreePlayerData(ref _connKey);

            return true;
        }

        private void StopThreadsAndDisconnectConnections()
        {
            // First, stop the worker threads.
            // This way, any new connection attempts will be ignored,
            // and there will not be any further processing on the connections we're forcing to disconnect.
            _stopCancellationTokenSource.Cancel();

            foreach (Thread thread in _threadList)
            {
                thread.Join();
            }
            _threadList.Clear();

            // Give a chance for any queued mainloop work to complete (remaining packets to process).
            _mainloop.WaitForMainWorkItemDrain();

            //
            // Force the disconnection of all connections.
            //

            ReadOnlySpan<byte> disconnectSpan = [0x00, 0x07];

            // Disconnect all players.
            HashSet<Player> playerSet = _objectPoolManager.PlayerSetPool.Get();
            try
            {
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (IsOurs(player))
                        {
                            if (!player.TryGetExtraData(_connKey, out PlayerConnection? playerConnection))
                                continue;

                            // Force the disconnection.
                            ProcessDisconnect(player, playerConnection, true);

                            playerSet.Add(player);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                foreach (Player player in playerSet)
                {
                    _playerData.FreePlayer(player);
                }
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(playerSet);
            }

            // Disconnect all client connections.
            _clientConnectionsLock.EnterWriteLock();

            try
            {
                foreach (ClientConnection clientConnection in _clientConnections.Values)
                {
                    // Force the tear down.
                    TearDownConnection(clientConnection, true);

                    // Send the disconnect packet.
                    SendRaw(clientConnection, disconnectSpan);

                    // Cleanup the encryptor now that the last send is complete.
                    if (clientConnection.Encryptor is not null)
                    {
                        clientConnection.Encryptor.Void(clientConnection);
                        _broker.ReleaseInterface(ref clientConnection.Encryptor, clientConnection.EncryptorName);
                    }

                    clientConnection.Status = ClientConnectionStatus.Disconnected;

                    // Notify the handler.
                    // We are already on the mainloop thread, but queue it anyway so that it's processed
                    // in the proper order (in case there are any reliable callbacks in the queue).
                    // The queue will be drained at the end.
                    _mainloop.QueueMainWorkItem(_mainloopWork_ClientConnectionHandlerDisconnected, clientConnection);
                }

                _clientConnections.Clear();
            }
            finally
            {
                _clientConnectionsLock.ExitWriteLock();
            }

            // Give a chance for any queued mainloop work to complete (reliable callbacks and IClientConnectionHandler.Disconnected).
            _mainloop.WaitForMainWorkItemDrain();
        }

        #endregion

        #region IRawNetwork Members

        void IRawNetwork.AppendConnectionInitHandler(ConnectionInitHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            _connectionInitLock.EnterWriteLock();

            try
            {
                _connectionInitHandlers.Add(handler);
            }
            finally
            {
                _connectionInitLock.ExitWriteLock();
            }
        }

        bool IRawNetwork.RemoveConnectionInitHandler(ConnectionInitHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            _connectionInitLock.EnterWriteLock();

            try
            {
                return _connectionInitHandlers.Remove(handler);
            }
            finally
            {
                _connectionInitLock.ExitWriteLock();
            }
        }

        bool IRawNetwork.RegisterPeerPacketHandler(PeerPacketHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            if (_peerPacketHandler is not null)
                return false;

            _peerPacketHandler = handler;
            return true;
        }

        bool IRawNetwork.UnregisterPeerPacketHandler(PeerPacketHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            if (_peerPacketHandler != handler)
                return false;

            _peerPacketHandler = null;
            return true;
        }

        void IRawNetwork.ReallyRawSend(SocketAddress remoteAddress, ReadOnlySpan<byte> data, ListenData ld)
        {
            ArgumentNullException.ThrowIfNull(remoteAddress);
            ArgumentNullException.ThrowIfNull(ld);

            if (data.Length < 1)
                throw new ArgumentOutOfRangeException(nameof(data), "There needs to be at least 1 byte to send.");

#if CFG_DUMP_RAW_PACKETS
            DumpPk($"SENDRAW: {data.Length} bytes", data);
#endif

            try
            {
                ld.GameSocket.SendTo(data, SocketFlags.None, remoteAddress);
            }
            catch (SocketException ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(Network), $"SocketException with error code {ex.ErrorCode} when sending to {remoteAddress} with game socket {ld.GameSocket.LocalEndPoint}. {ex}");
                return;
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(Network), $"Exception when sending to {remoteAddress} with game socket {ld.GameSocket.LocalEndPoint}. {ex}");
                return;
            }

            Interlocked.Add(ref _globalStats.BytesSent, (ulong)data.Length);
            Interlocked.Increment(ref _globalStats.PacketsSent);
        }

        Player? IRawNetwork.NewConnection(ClientType clientType, IPEndPoint remoteEndpoint, string? encryptorName, ListenData listenData)
        {
            ArgumentNullException.ThrowIfNull(remoteEndpoint);
            ArgumentNullException.ThrowIfNull(listenData);

            // certain ports may have restrictions on client types
            if ((clientType == ClientType.VIE && !listenData.AllowVIE)
                || (clientType == ClientType.Continuum && !listenData.AllowContinuum))
            {
                return null;
            }

            // try to find a matching player for the endpoint
            SocketAddress remoteAddress = remoteEndpoint.Serialize();
            if (_playerConnections.TryGetValue(remoteAddress, out Player? player))
            {
                // We found it. If its status is Connected, just return the pid.
                // It means we have to redo part of the connection init.

                if (player.Status <= PlayerState.Connected)
                {
                    return player;
                }
                else
                {
                    // otherwise, something is horribly wrong. make a note to this effect
                    _logManager.LogP(LogLevel.Error, nameof(Network), player, "NewConnection called for an established address.");
                    return null;
                }
            }

            player = _playerData.NewPlayer(clientType);

            if (!player.TryGetExtraData(_connKey, out PlayerConnection? conn))
            {
                _logManager.LogP(LogLevel.Error, nameof(Network), player, "NewConnection created a new player, but PlayerConnection not found.");
                return null;
            }

            IEncrypt? encryptor = null;
            if (encryptorName is not null)
            {
                encryptor = _broker.GetInterface<IEncrypt>(encryptorName);

                if (encryptor is null)
                {
                    _logManager.LogP(LogLevel.Error, nameof(Network), player, $"NewConnection called to use IEncrypt '{encryptorName}', but not found.");
                    return null;
                }
            }

            conn.Initialize(encryptor, encryptorName, _bandwidthLimiterProvider.New());
            conn.Player = player;

            // copy data from ListenData
            conn.SendSocket = listenData.GameSocket;
            player.ConnectAs = listenData.ConnectAs;

            player.IPAddress = remoteEndpoint.Address;

            player.ClientName = clientType switch
            {
                ClientType.VIE => "<ss/vie client>",
                ClientType.Continuum => "<continuum>",
                _ => "<unknown game client>",
            };

            conn.RemoteAddress = remoteAddress;
            conn.RemoteEndpoint = remoteEndpoint;
            _playerConnections[remoteAddress] = player;

            _playerData.WriteLock();
            try
            {
                player.Status = PlayerState.Connected;
            }
            finally
            {
                _playerData.WriteUnlock();
            }

            _logManager.LogP(LogLevel.Drivel, nameof(Network), player, $"New connection from {remoteEndpoint}.");

            return player;
        }

        #endregion

        #region INetwork Members

        void INetwork.SendToOne(Player player, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            SendToOne(player, data, flags);
        }

        void INetwork.SendToOne<TData>(Player player, ref readonly TData data, NetSendFlags flags) where TData : struct
        {
            ((INetwork)this).SendToOne(player, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in data, 1)), flags);
        }

        void INetwork.SendToArena(Arena? arena, Player? except, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            if (data.Length < 1)
                return;

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.Lock();

                try
                {
                    foreach (Player p in _playerData.Players)
                    {
                        if (p.Status == PlayerState.Playing
                            && (p.Arena == arena || arena is null)
                            && p != except
                            && IsOurs(p))
                        {
                            set.Add(p);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                SendToSet(set, data, flags);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        void INetwork.SendToArena<TData>(Arena? arena, Player? except, ref readonly TData data, NetSendFlags flags) where TData : struct
        {
            ((INetwork)this).SendToArena(arena, except, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in data, 1)), flags);
        }

        void INetwork.SendToSet(HashSet<Player> set, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            SendToSet(set, data, flags);
        }

        void INetwork.SendToSet<TData>(HashSet<Player> set, ref readonly TData data, NetSendFlags flags) where TData : struct
        {
            ((INetwork)this).SendToSet(set, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in data, 1)), flags);
        }

        void INetwork.SendWithCallback(Player player, ReadOnlySpan<byte> data, ReliableDelegate callback)
        {
            ReliablePlayerCallbackInvoker invoker = _reliablePlayerCallbackInvokerPool.Get();
            invoker.SetCallback(callback, ReliableCallbackExecutionOption.Mainloop);
            if (!SendWithCallback(player, data, invoker))
            {
                invoker.Dispose();
            }
        }

        void INetwork.SendWithCallback<TData>(Player player, ref readonly TData data, ReliableDelegate callback)
        {
            ((INetwork)this).SendWithCallback(player, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in data, 1)), callback);
        }

        void INetwork.SendWithCallback<TState>(Player player, ReadOnlySpan<byte> data, ReliableDelegate<TState> callback, TState state)
        {
            ReliablePlayerCallbackInvoker<TState> invoker = _objectPoolManager.GetPool<ReliablePlayerCallbackInvoker<TState>>().Get();
            invoker.SetCallback(callback, state, ReliableCallbackExecutionOption.Mainloop);
            if (!SendWithCallback(player, data, invoker))
            {
                invoker.Dispose();
            }
        }

        void INetwork.SendWithCallback<TData, TState>(Player player, ref readonly TData data, ReliableDelegate<TState> callback, TState state)
        {
            ((INetwork)this).SendWithCallback(player, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in data, 1)), callback, state);
        }

        void INetwork.SendToTarget(ITarget target, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            if (data.Length < 1)
                return;

            HashSet<Player> set = _objectPoolManager.PlayerSetPool.Get();

            try
            {
                _playerData.TargetToSet(target, set);
                SendToSet(set, data, flags);
            }
            finally
            {
                _objectPoolManager.PlayerSetPool.Return(set);
            }
        }

        void INetwork.SendToTarget<TData>(ITarget target, ref readonly TData data, NetSendFlags flags)
        {
            ((INetwork)this).SendToTarget(target, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in data, 1)), flags);
        }

        bool INetwork.SendSized<T>(Player player, int len, GetSizedSendDataDelegate<T> requestCallback, T state)
        {
            if (player is null)
                return false;

            if (len <= 0)
                return false;

            if (requestCallback is null)
                return false;

            if (!IsOurs(player))
            {
                _logManager.LogP(LogLevel.Drivel, nameof(Network), player, "Tried to send sized data to non-udp client.");
                return false;
            }

            if (!player.TryGetExtraData(_connKey, out PlayerConnection? conn))
                return false;

            _playerData.Lock();

            try
            {
                if (player.Status >= PlayerState.TimeWait)
                {
                    // The player is being disconnected. Do not allow a sized send to be started.
                    return false;
                }

                // Add the sized send while continuing to hold the global player data lock (so that the status can't be changed while we're adding).
                SizedSendData<T> sizedSendData = SizedSendData<T>.Pool.Get();
                sizedSendData.Initialize(requestCallback, state, len);

                LinkedListNode<ISizedSendData> node = _sizedSendDataNodePool.Get();
                node.Value = sizedSendData;

                lock (conn.SizedSendLock)
                {
                    conn.SizedSends.AddLast(node);
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            // Add the connection to the sized send queue to be processed.
            _sizedSendQueue.TryEnqueue(conn);

            return true;
        }

        void INetwork.AddPacket(C2SPacketType packetType, PacketHandler handler)
        {
            if (handler is null)
                return;

            int packetTypeInt = (int)packetType;
            if (packetTypeInt >= 0 && packetTypeInt < _handlers.Length)
            {
                PacketHandler? d = _handlers[packetTypeInt];
                _handlers[packetTypeInt] = (d is null) ? handler : (d += handler);
            }
            else if ((packetTypeInt & 0xFF) == 0)
            {
                int b2 = packetTypeInt >> 8;

                if (b2 >= 0 && b2 < _nethandlers.Length && _nethandlers[b2] is null)
                {
                    _nethandlers[b2] = handler;
                }
            }
        }

        void INetwork.RemovePacket(C2SPacketType packetType, PacketHandler handler)
        {
            if (handler is null)
                return;

            int packetTypeInt = (int)packetType;
            if (packetTypeInt >= 0 && packetTypeInt < _handlers.Length)
            {
                PacketHandler? d = _handlers[packetTypeInt];
                if (d is not null)
                {
                    _handlers[packetTypeInt] = (d -= handler);
                }
            }
            else if ((packetTypeInt & 0xFF) == 0)
            {
                int b2 = packetTypeInt >> 8;

                if (b2 >= 0 && b2 < _nethandlers.Length && _nethandlers[b2] == handler)
                {
                    _nethandlers[b2] = null;
                }
            }
        }

        void INetwork.AddSizedPacket(C2SPacketType packetType, SizedPacketHandler handler)
        {
            if (handler is null)
                return;

            int packetTypeInt = (int)packetType;
            if (packetTypeInt >= 0 && packetTypeInt < _sizedhandlers.Length)
            {
                SizedPacketHandler? d = _sizedhandlers[packetTypeInt];
                _sizedhandlers[packetTypeInt] = (d is null) ? handler : (d += handler);
            }
        }

        void INetwork.RemoveSizedPacket(C2SPacketType packetType, SizedPacketHandler handler)
        {
            if (handler is null)
                return;

            int packetTypeInt = (int)packetType;
            if (packetTypeInt >= 0 && packetTypeInt < _sizedhandlers.Length)
            {
                SizedPacketHandler? d = _sizedhandlers[packetTypeInt];
                if (d is not null)
                {
                    _sizedhandlers[packetTypeInt] = (d -= handler);
                }
            }
        }

        IReadOnlyNetStats INetwork.GetStats()
        {
            ulong objectsCreated = (ulong)_bufferPool.ObjectsCreated;
            Interlocked.Exchange(ref _globalStats.BuffersUsed, objectsCreated - (ulong)_bufferPool.ObjectsAvailable);
            Interlocked.Exchange(ref _globalStats.BuffersTotal, objectsCreated);

            return _globalStats;
        }

        void INetwork.GetConnectionStats(Player player, ref NetConnectionStats stats)
        {
            ArgumentNullException.ThrowIfNull(player);

            if (!player.TryGetExtraData(_connKey, out PlayerConnection? conn))
                return;

            GetConnectionStats(conn, ref stats);
        }

        ProxyUsage INetwork.GetProxyUsage(Player player)
        {
            if (!player.IsStandard)
                return ProxyUsage.Undetermined;

            if (player.ClientReportedServerIPv4Address == 0 || player.ClientReportedBoundPort == 0)
            {
                return ProxyUsage.SOCKS5;
            }
            else if ((player.ClientReportedServerIPv4Address & 0xFF000000) == 0x7F000000) // 127.x.x.x
            {
                return ProxyUsage.LocalProxy;
            }
            else
            {
                if (!player.TryGetExtraData(_connKey, out PlayerConnection? connection))
                    return ProxyUsage.Undetermined;

                Span<byte> ipSpan = stackalloc byte[4];
                uint? listenSocketIPv4Address = null;

                foreach (ListenData listenData in _listenDataList)
                {
                    if (listenData.GameSocket == connection.SendSocket)
                    {
                        if (listenData.GameSocket.LocalEndPoint is not IPEndPoint localIPEndPoint)
                            return ProxyUsage.Undetermined;

                        IPAddress ipAddress = localIPEndPoint.Address;
                        if (ipAddress.AddressFamily == AddressFamily.InterNetwork) // IPv4
                        {
                            if (ipAddress.TryWriteBytes(ipSpan, out int bytesWritten) && bytesWritten == 4)
                            {
                                listenSocketIPv4Address = BinaryPrimitives.ReadUInt32BigEndian(ipSpan);
                                break;
                            }
                        }
                    }
                }

                if (listenSocketIPv4Address is null || listenSocketIPv4Address.Value == 0) // 0 is IPAddress.Any
                {
                    return ProxyUsage.NotConfigured;
                }
                else if (listenSocketIPv4Address != player.ClientReportedServerIPv4Address)
                {
                    return ProxyUsage.CustomProxy;
                }
                else if (connection.RemoteEndpoint!.Port != player.ClientReportedBoundPort)
                {
                    return ProxyUsage.NAT;
                }
                else
                {
                    return ProxyUsage.NoProxy;
                }
            }
        }

        TimeSpan INetwork.GetLastReceiveTimeSpan(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);

            if (!player.TryGetExtraData(_connKey, out PlayerConnection? conn))
                return TimeSpan.Zero;

            return Stopwatch.GetElapsedTime(Interlocked.Read(ref conn.LastReceiveTimestamp));
        }

        bool INetwork.TryGetListenData(int index, [MaybeNullWhen(false)] out IPEndPoint endPoint, out string? connectAs)
        {
            if (index >= _listenDataList.Count)
            {
                endPoint = default;
                connectAs = default;
                return false;
            }

            ListenData ld = _listenDataList[index];
            endPoint = ld.GameSocket.LocalEndPoint as IPEndPoint;

            if (endPoint is null)
            {
                connectAs = default;
                return false;
            }

            connectAs = ld.ConnectAs;
            return true;
        }

        bool INetwork.TryGetPopulationStats(string connectAs, out uint total, out uint playing)
        {
            if (!_pingData.ConnectAsPopulationStats.TryGetValue(connectAs, out PopulationStats? stats))
            {
                total = default;
                playing = default;
                return false;
            }

            stats.GetStats(out total, out playing);
            return true;
        }

        IReadOnlyList<ListenData> INetwork.Listening => _readOnlyListenData;

        #endregion

        #region INetworkClient Members

        IClientConnection? INetworkClient.MakeClientConnection(IPEndPoint endPoint, IClientConnectionHandler handler, string encryptorName, string bandwidthLimiterProviderName)
        {
            ArgumentNullException.ThrowIfNull(endPoint);
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentException.ThrowIfNullOrWhiteSpace(encryptorName);
            ArgumentException.ThrowIfNullOrWhiteSpace(bandwidthLimiterProviderName);

            if (_clientSocket is null)
                throw new InvalidOperationException(Constants.ErrorMessages.ModuleNotLoaded);

            IClientEncrypt? encryptor = _broker.GetInterface<IClientEncrypt>(encryptorName);
            if (encryptor is null)
            {
                _logManager.LogM(LogLevel.Error, nameof(Network), $"Unable to find an {nameof(IClientEncrypt)} named '{encryptorName}'.");
                return null;
            }

            IBandwidthLimiterProvider? bandwidthLimitProvider = _broker.GetInterface<IBandwidthLimiterProvider>(bandwidthLimiterProviderName);
            if (bandwidthLimitProvider is null)
            {
                _logManager.LogM(LogLevel.Error, nameof(Network), $"Unable to find an {nameof(IBandwidthLimiterProvider)} named '{bandwidthLimiterProviderName}'.");
                _broker.ReleaseInterface(ref encryptor, encryptorName);
                return null;
            }

            ClientConnection clientConnection = _clientConnectionPool.Get();
            clientConnection.Initialize(endPoint, _clientSocket, handler, encryptor, encryptorName, bandwidthLimitProvider, bandwidthLimiterProviderName);

            bool added;

            _clientConnectionsLock.EnterWriteLock();

            try
            {
                added = _clientConnections.TryAdd(clientConnection.RemoteAddress!, clientConnection);
            }
            finally
            {
                _clientConnectionsLock.ExitWriteLock();
            }

            if (!added)
            {
                _logManager.LogM(LogLevel.Error, nameof(Network), $"Attempt to make a client connection to {endPoint} when one already exists.");

                clientConnection.Encryptor!.Void(clientConnection);
                _broker.ReleaseInterface(ref clientConnection.Encryptor, clientConnection.EncryptorName);

                clientConnection.BandwidthLimiterProvider!.Free(clientConnection.BandwidthLimiter!);
                clientConnection.BandwidthLimiter = null;

                _broker.ReleaseInterface(ref clientConnection.BandwidthLimiterProvider, clientConnection.BandwidthLimiterProviderName);
                clientConnection.BandwidthLimiterProviderName = null;

                _clientConnectionPool.Return(clientConnection);
                return null;
            }

            ConnectionInitPacket packet = new((int)(_prng.Get32() | 0x80000000), 1);
            SendRaw(clientConnection, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1)));

            return clientConnection;
        }

        void INetworkClient.SendPacket(IClientConnection connection, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            ArgumentNullException.ThrowIfNull(connection);

            if (connection is not ClientConnection clientConnection)
                throw new ArgumentException("Unsupported client connection. It must be created by this module.", nameof(connection));

            SendToOne(clientConnection, data, flags);
        }

        void INetworkClient.SendPacket<T>(IClientConnection connection, ref T data, NetSendFlags flags)
        {
            ArgumentNullException.ThrowIfNull(connection);

            if (connection is not ClientConnection clientConnection)
                throw new ArgumentException("Unsupported client connection. It must be created by this module.", nameof(connection));

            ((INetworkClient)this).SendPacket(clientConnection, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1)), flags);
        }

        void INetworkClient.SendWithCallback(IClientConnection connection, ReadOnlySpan<byte> data, ClientReliableCallback callback)
        {
            ArgumentNullException.ThrowIfNull(connection);

            if (connection is not ClientConnection clientConnection)
                throw new ArgumentException("Unsupported client connection. It must be created by this module.", nameof(connection));

            ReliableClientConnectionCallbackInvoker invoker = _reliableClientConnectionCallbackInvokerPool.Get();
            invoker.SetCallback(callback, ReliableCallbackExecutionOption.Mainloop);
            if (!SendWithCallback(clientConnection, data, invoker))
            {
                invoker.Dispose();
            }
        }

        void INetworkClient.DropConnection(IClientConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            if (connection is not ClientConnection clientConnection)
                throw new ArgumentException("Unsupported client connection. It must be created by this module.", nameof(connection));

            SetDisconnecting(clientConnection);
        }

        void INetworkClient.GetConnectionStats(IClientConnection connection, ref NetConnectionStats stats)
        {
            ArgumentNullException.ThrowIfNull(connection);

            if (connection is not ClientConnection clientConnection)
                throw new ArgumentException("Unsupported client connection. It must be created by this module.", nameof(connection));

            GetConnectionStats(clientConnection, ref stats);
        }

        #endregion

        #region Core packet handlers (oohandlers)

        private void CorePacket_KeyResponse(Span<byte> data, ConnData conn, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length != 6)
                return;

            if (conn is ClientConnection clientConnection)
            {
                _clientConnectionsLock.EnterWriteLock();
                try
                {
                    if (clientConnection.Status != ClientConnectionStatus.Connecting)
                        return;

                    clientConnection.Status = ClientConnectionStatus.Connected;
                }
                finally
                {
                    _clientConnectionsLock.ExitWriteLock();
                }

                _mainloop.QueueMainWorkItem(MainloopWork_ClientConnectionHandlerConnected, clientConnection);
            }
            else if (conn is PlayerConnection playerConnection)
            {
                _logManager.LogP(LogLevel.Malicious, nameof(Network), playerConnection.Player!, "Got key response packet.");
            }


            // Local helper function for calling the client connection handler on the mainloop thread
            static void MainloopWork_ClientConnectionHandlerConnected(ClientConnection clientConnection)
            {
                clientConnection?.Handler.Connected();
            }
        }

        private void CorePacket_Reliable(Span<byte> data, ConnData conn, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length < 7) // at least enough for the header + 1 byte of data
            {
                return;
            }

            ref readonly ReliableHeader rp = ref MemoryMarshal.AsRef<ReliableHeader>(data);
            int sn = rp.SeqNum;

            bool added;
            bool isDuplicate;
            bool canProcess = false;
            int? currentSequenceNum = null;

            // Get the buffer to use but don't write to it yet unless we're able to add it.
            SubspaceBuffer buffer = _bufferPool.Get();

            lock (conn.ReliableLock)
            {
                added = conn.ReliableBuffer.TryAdd(sn, buffer, out isDuplicate);

                if (added)
                {
                    buffer.Conn = conn;
                    buffer.ReceiveFlags = flags | NetReceiveFlags.Reliable;
                    data.CopyTo(buffer.Bytes);
                    buffer.NumBytes = data.Length;

                    canProcess = (sn == conn.ReliableBuffer.CurrentSequenceNum);
                }
                else
                {
                    if (!isDuplicate)
                    {
                        // Not able to add and it wasn't a duplicate.
                        // While still holding the lock, read the current sequence # so that we can log it later.
                        currentSequenceNum = conn.ReliableBuffer.CurrentSequenceNum;
                    }
                }
            }

            if (added)
            {
                Interlocked.Increment(ref conn.ReliablePacketsReceived);

                if (canProcess)
                {
                    // It's ready to be processed. Queue it to be processed by the RelThread.
                    Interlocked.Increment(ref conn.ProcessingHolds);
                    if (!_relQueue.TryEnqueue(conn))
                    {
                        Interlocked.Decrement(ref conn.ProcessingHolds);
                    }
                }
            }
            else
            {
                buffer.Dispose();

                if (isDuplicate)
                {
                    Interlocked.Increment(ref conn.RelDups);
                }
                else
                {
                    if (conn is PlayerConnection playerConnection)
                        _logManager.LogP(LogLevel.Drivel, nameof(Network), playerConnection.Player!, $"Reliable packet with too big delta (current:{currentSequenceNum} received:{sn}).");
                    else if (conn is ClientConnection)
                        _logManager.LogM(LogLevel.Drivel, nameof(Network), $"(client connection) Reliable packet with too big delta (current:{currentSequenceNum} received:{sn}).");

                    // just drop it
                    return;
                }
            }

            if (added || isDuplicate)
            {
                // send the ack
                AckPacket ap = new(sn);

                SendOrBufferPacket(
                    conn,
                    MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref ap, 1)),
                    NetSendFlags.Ack);
            }
        }

        private void CorePacket_Ack(Span<byte> data, ConnData conn, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length != AckPacket.Length)
                return;

            ref readonly AckPacket ack = ref MemoryMarshal.AsRef<AckPacket>(data);
            int seqNum = ack.SeqNum;

            SubspaceBuffer? buffer = null;
            int? rtt = null;

            lock (conn.OutLock)
            {
                LinkedList<SubspaceBuffer> outList = conn.OutList[(int)BandwidthPriority.Reliable];
                LinkedListNode<SubspaceBuffer>? nextNode = null;
                for (LinkedListNode<SubspaceBuffer>? node = outList.First; node is not null; node = nextNode)
                {
                    nextNode = node.Next;

                    SubspaceBuffer checkBuffer = node.Value;
                    ref readonly ReliableHeader header = ref MemoryMarshal.AsRef<ReliableHeader>(checkBuffer.Bytes);
                    if (header.SeqNum == seqNum)
                    {
                        buffer = checkBuffer;
                        outList.Remove(node);
                        _bufferNodePool.Return(node);

                        // Update the connection's round trip time estimate.
                        // This can only be done if the packet wasn't resent.
                        // This is because if it was resent, we wouldn't be able to tell which send the ACK was in response to.
                        if (buffer.Tries == 1)
                        {
                            // The packet was not resent.
                            // This means we can calculate an accurate round trip time.
                            rtt = (int)Stopwatch.GetElapsedTime(buffer.LastTryTimestamp!.Value).TotalMilliseconds;
                            if (rtt < 0)
                            {
                                _logManager.LogM(LogLevel.Error, nameof(Network), $"Negative rtt ({rtt}); clock going backwards.");
                                rtt = 100;
                            }

                            int dev = conn.AverageRoundTripTime - rtt.Value;
                            if (dev < 0)
                                dev = -dev;

                            conn.AverageRoundTripDeviation = (conn.AverageRoundTripDeviation * 3 + dev) / 4;
                            conn.AverageRoundTripTime = (conn.AverageRoundTripTime * 7 + rtt.Value) / 8;

                            // For player connections, rtt will be used to update player lags stats outside of the lock.
                        }
                        else
                        {
                            // The packet was resent, so we can't calculate an accurate round trip time.
                            // However, the current RTT estimation might be too low, causing us to resend too early.
                            if (conn.AverageRoundTripDeviation < conn.AverageRoundTripTime)
                            {
                                // Add a little to the deviation, so that we'll wait longer before resending.
                                conn.AverageRoundTripDeviation = int.Min(conn.AverageRoundTripDeviation + 10, conn.AverageRoundTripTime);
                            }
                        }

                        // handle limit adjustment
                        conn.BandwidthLimiter!.AdjustForAck();

                        break;
                    }
                }
            }

            if (buffer is not null)
            {
                if (buffer.CallbackInvoker is not null)
                {
                    ExecuteReliableCallback(buffer.CallbackInvoker, conn, true);
                    buffer.CallbackInvoker = null;
                }

                buffer.Dispose();
            }
            else
            {
                Interlocked.Increment(ref conn.AckDups);
            }

            if (rtt.HasValue && _lagCollect is not null && conn is PlayerConnection playerConnection)
            {
                Player? player = playerConnection.Player;
                if (player is not null)
                {
                    _lagCollect.RelDelay(player, rtt.Value);
                }
            }
        }

        private void CorePacket_SyncRequest(Span<byte> data, ConnData conn, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length != TimeSyncRequest.Length)
                return;

            ref readonly TimeSyncRequest request = ref MemoryMarshal.AsRef<TimeSyncRequest>(data);
            uint clientTime = request.Time;
            uint serverTime = ServerTick.Now;

            // note: this bypasses bandwidth limits
            TimeSyncResponse response = new(clientTime, serverTime);
            SendRaw(conn, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref response, 1)));

            // submit data to lagdata
            if (_lagCollect is not null && conn is PlayerConnection playerConnection)
            {
                Player? player = playerConnection.Player;
                if (player is null)
                    return;

                TimeSyncData timeSyncData = new()
                {
                    ServerPacketsReceived = Interlocked.CompareExchange(ref conn.PacketsReceived, 0, 0),
                    ServerPacketsSent = Interlocked.CompareExchange(ref conn.PacketsSent, 0, 0),
                    ClientPacketsReceived = request.PacketsReceived,
                    ClientPacketsSent = request.PacketsSent,
                    ServerTime = serverTime,
                    ClientTime = clientTime,
                };

                _lagCollect.TimeSync(player, in timeSyncData);
            }
        }

        private void CorePacket_Drop(Span<byte> data, ConnData conn, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length != 2)
                return;

            if (conn is PlayerConnection playerConnection)
            {
                Player? player = playerConnection.Player;
                if (player is not null)
                {
                    _playerData.KickPlayer(player);
                }
            }
            else if (conn is ClientConnection clientConnection)
            {
                SetDisconnecting(clientConnection);
            }
        }

        private void CorePacket_BigData(Span<byte> data, ConnData conn, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length < 3) // 0x00, [0x08 or 0x09], and then at least one byte of data
                return;

            BigReceive bigReceive;
            bool dispose = false;

            lock (conn.BigLock)
            {
                // Get a BigReceive object if we don't already have one and give it to the connection to 'own'.
                bigReceive = (conn.BigReceive ??= _bigReceivePool.Get());

                // Append the data.
                if (!bigReceive.IsOverflow // not already overflowed
                    & bigReceive.Append(data[2..], flags)) // data only, header removed
                {
                    if (conn is PlayerConnection playerConnection)
                        _logManager.LogP(LogLevel.Malicious, nameof(Network), playerConnection.Player!, $"Ignoring big data (> {Constants.MaxBigPacket}).");
                    else if (conn is ClientConnection)
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), $"(client connection) Ignoring big data (> {Constants.MaxBigPacket}).");
                };

                if (data[1] == 0x08)
                    return;

                // Getting here means it was a 0x00 0x09 (end of "Big" data packet stream).
                conn.BigReceive = null;

                if (bigReceive.IsOverflow)
                {
                    dispose = true;

                    if (conn is PlayerConnection playerConnection)
                        _logManager.LogP(LogLevel.Malicious, nameof(Network), playerConnection.Player!, $"Ignored {bigReceive.Length} bytes of big data (> {Constants.MaxBigPacket}).");
                    else if (conn is ClientConnection)
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), $"(client connection) Ignored {bigReceive.Length} bytes of big data (> {Constants.MaxBigPacket}).");
                }
                else
                {
                    // We have all of the data. Process it on the mainloop thread
                    // Ownership of the BigReceive object is transferred to the workitem. The workitem is responsible for disposing it.
                    Interlocked.Increment(ref conn.ProcessingHolds);
                    if (!_mainloop.QueueMainWorkItem(_mainloopWork_CallBigPacketHandlers, new BigPacketWork(conn, bigReceive)))
                    {
                        Interlocked.Decrement(ref conn.ProcessingHolds);
                        dispose = true;
                    }
                }
            }

            if (dispose)
            {
                bigReceive.Dispose();
            }
        }

        private void MainloopWork_CallBigPacketHandlers(BigPacketWork work)
        {
            try
            {
                if (work.ConnData is null || work.BigReceive is null || work.BigReceive.Buffer.IsEmpty || work.BigReceive.Length < 1)
                    return;

                CallPacketHandlers(work.ConnData, work.BigReceive.Buffer[..work.BigReceive.Length], work.BigReceive.Flags);
            }
            finally
            {
                // Return the BigReceive object to its pool.
                work.BigReceive?.Dispose();

                if (work.ConnData is not null)
                {
                    Interlocked.Decrement(ref work.ConnData.ProcessingHolds);
                }
            }
        }

        private void CorePacket_SizedData(Span<byte> data, ConnData conn, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length < 7)
                return;

            ref readonly SizedHeader header = ref MemoryMarshal.AsRef<SizedHeader>(data);
            int size = header.Size;
            data = data[SizedHeader.Length..];

            lock (conn.BigLock)
            {
                // only handle sized packets for player connections, not client connections
                if (conn is not PlayerConnection playerConnection)
                    return;

                Player? player = playerConnection.Player;
                if (player is null)
                    return;

                if (conn.SizedRecv.Offset == 0)
                {
                    // first packet
                    int type = data[0];
                    if (type < _sizedhandlers.Length)
                    {
                        conn.SizedRecv.Type = type;
                        conn.SizedRecv.TotalLength = size;
                    }
                    else
                    {
                        EndSizedReceive(player, false);
                    }
                }

                if (conn.SizedRecv.TotalLength != size)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(Network), player, "Length mismatch in sized packet.");
                    EndSizedReceive(player, false);
                }
                else if ((conn.SizedRecv.Offset + data.Length) > size)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(Network), player, "Sized packet overflow.");
                    EndSizedReceive(player, false);
                }
                else
                {
                    _sizedhandlers[conn.SizedRecv.Type]?.Invoke(player, data, conn.SizedRecv.Offset, size);

                    conn.SizedRecv.Offset += data.Length;

                    if (conn.SizedRecv.Offset >= size)
                        EndSizedReceive(player, true); // sized receive is complete
                }
            }
        }

        private void CorePacket_CancelSized(Span<byte> data, ConnData conn, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length != 2)
                return;

            bool cancelled = false;

            // The client has requested to cancel the sized transfer.
            // Find the first sized send that is not already cancelled and cancel it.
            lock (conn.SizedSendLock)
            {
                LinkedListNode<ISizedSendData>? node = conn.SizedSends.First;
                while (node is not null)
                {
                    LinkedListNode<ISizedSendData>? next = node.Next;

                    ISizedSendData ssd = node.Value;
                    if (!ssd.IsCancellationRequested)
                    {
                        ssd.Cancel(true);
                        cancelled = true;
                        break;
                    }

                    node = node.Next;
                }
            }

            if (cancelled)
            {
                // Make sure the connection is in the queue to be processed.
                _sizedSendQueue.TryEnqueue(conn);
            }
        }

        private void CorePacket_SizedCancelled(Span<byte> data, ConnData conn, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length != 2)
                return;

            if (conn is PlayerConnection playerConnection)
            {
                Player? player = playerConnection.Player;
                if (player is null)
                    return;

                lock (conn.BigLock)
                {
                    EndSizedReceive(player, false);
                }
            }
        }

        private void CorePacket_Grouped(Span<byte> data, ConnData conn, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length < 4)
                return;

            if (data.Length > Constants.MaxGroupedPacketLength)
                return;

            data = data[2..];
            while (data.Length > 0)
            {
                int len = data[0];
                if (len > data.Length - 1)
                    break;

                ProcessBuffer(conn, data.Slice(1, len), flags | NetReceiveFlags.Grouped);

                data = data[(1 + len)..];
            }
        }

        private void CorePacket_Special(Span<byte> data, ConnData conn, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length < 2)
                return;

            if (conn is not PlayerConnection playerConnection)
                return;

            Player? player = playerConnection.Player;
            if (player is null)
                return;

            int t2 = data[1];

            if (t2 < _nethandlers.Length)
            {
                _nethandlers[t2]?.Invoke(player, data, flags);
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _stopCancellationTokenSource.Dispose();
            _clientConnectionsLock.Dispose();
            _relQueue.Dispose();
            _sizedSendQueue.Dispose();
        }

        #endregion

        #region Worker Threads

        private void ReceiveThread()
        {
            List<Socket> socketList = new(_listenDataList.Count * 2 + 1);
            List<Socket> checkReadList = new(_listenDataList.Count * 2 + 1);

            Dictionary<EndPoint, (char Type, ListenData ListenData)> endpointLookup
                = new(_listenDataList.Count * 2);

            foreach (ListenData ld in _listenDataList)
            {
                if (ld.GameSocket is not null)
                {
                    socketList.Add(ld.GameSocket);
                    endpointLookup.Add(ld.GameSocket.LocalEndPoint!, ('G', ld));
                }

                if (ld.PingSocket is not null)
                {
                    socketList.Add(ld.PingSocket);
                    endpointLookup.Add(ld.PingSocket.LocalEndPoint!, ('P', ld));
                }
            }

            if (_clientSocket is not null)
            {
                socketList.Add(_clientSocket);
            }

            while (true)
            {
                try
                {
                    if (_stopToken.IsCancellationRequested)
                        return;

                    checkReadList.Clear();
                    checkReadList.AddRange(socketList);

                    Socket.Select(checkReadList, null, null, MICROSECONDS_PER_MILLISECOND * 1000);

                    if (_stopToken.IsCancellationRequested)
                        return;

                    foreach (Socket socket in checkReadList)
                    {
                        if (endpointLookup.TryGetValue(socket.LocalEndPoint!, out var tuple))
                        {
                            if (tuple.Type == 'G')
                                HandleGamePacketReceived(tuple.ListenData);
                            else if (tuple.Type == 'P')
                                HandlePingPacketReceived(tuple.ListenData);
                        }
                        else if (socket == _clientSocket)
                        {
                            HandleClientPacketReceived();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(LogManager), $"Caught an exception in ReceiveThread. {ex}");
                }
            }

            void HandleGamePacketReceived(ListenData ld)
            {
                if (ld is null)
                    return;

                int bytesReceived;
                SocketAddress receivedAddress = ld.GameSocket.AddressFamily == AddressFamily.InterNetworkV6 ? _receiveSocketAddressV6 : _receiveSocketAddressV4;

                try
                {
                    bytesReceived = ld.GameSocket.ReceiveFrom(_receiveBuffer, SocketFlags.None, receivedAddress);
                }
                catch (SocketException ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"SocketException with error code {ex.ErrorCode} when receiving from game socket {ld.GameSocket.LocalEndPoint}. {ex}");
                    return;
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"Exception when receiving from game socket {ld.GameSocket.LocalEndPoint}. {ex}");
                    return;
                }

                if (bytesReceived <= 0)
                {
                    return;
                }

                Span<byte> data = _receiveBuffer.AsSpan(0, bytesReceived);

                // TODO: Add some type of denial of service / flood detection for bad packet sizes? repeated connection init attempts over a threshold? block by ip/port?  How to tell if it's through a SOCKS proxy?

                if (data.Length >= PeerPacketHeader.Length && data[0] == 0x00 && data[1] == 0x01 && data[6] == 0x0FF)
                {
                    // Received what appears to be a peer packet.
                    if (_peerPacketHandler is null)
                    {
                        _logManager.LogM(LogLevel.Drivel, nameof(Network), $"Received a peer packet ({bytesReceived} bytes) from {receivedAddress}, but peer functionality is not enabled.");
                        return;
                    }

                    if (bytesReceived > Constants.MaxPeerPacket)
                    {
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), $"Received a peer packet that is too large ({bytesReceived} bytes) from {receivedAddress}.");
                        return;
                    }

                    _peerPacketHandler(receivedAddress, data);
                    return;
                }

                bool isConnectionInitPacket = IsConnectionInitPacket(data);

                if (bytesReceived > Constants.MaxPacket)
                {
                    _logManager.LogM(LogLevel.Malicious, nameof(Network), $"Received a {(isConnectionInitPacket ? "connection init" : "game")} packet that is too large ({bytesReceived} bytes) from {receivedAddress}.");
                    return;
                }

#if CFG_DUMP_RAW_PACKETS
                DumpPk($"RECV GAME DATA: {bytesReceived} bytes", data);
#endif

                if (!_playerConnections.TryGetValue(receivedAddress, out Player? player))
                {
                    // This might be a new connection. Make sure it's really a connection init packet.
                    if (isConnectionInitPacket)
                    {
                        ProcessConnectionInit(receivedAddress, data, ld);
                    }
#if CFG_LOG_STUPID_STUFF
                    else if (bytesReceived > 1)
                    {
                        _logManager.LogM(LogLevel.Drivel, nameof(Network), $"Received data ({data[0]:X2} {data[1]:X2} ; {bytesReceived} bytes) before connection established.");
                    }
                    else
                    {
                        _logManager.LogM(LogLevel.Drivel, nameof(Network), $"Received data ({data[0]:X2} ; {bytesReceived} bytes) before connection established.");
                    }
#endif
                    return;
                }

                if (!player.TryGetExtraData(_connKey, out PlayerConnection? conn))
                {
                    return;
                }

                PlayerState status;
                PlayerState whenLoggedIn;
                _playerData.Lock();
                try
                {
                    status = player.Status;
                    whenLoggedIn = player.WhenLoggedIn;

                    if (status > PlayerState.TimeWait)
                    {
                        _logManager.LogP(LogLevel.Warn, nameof(Network), player, $"Packet received from bad state {status}.");
                        return;
                    }

                    if (status >= PlayerState.LeavingZone || whenLoggedIn >= PlayerState.LeavingZone)
                    {
                        // The player is on their way out, ignore it.
                        return;
                    }

                    Interlocked.Increment(ref conn.ProcessingHolds);
                }
                finally
                {
                    _playerData.Unlock();
                }

                try
                {
                    if (isConnectionInitPacket)
                    {
                        // Here, we have a connection init, but it's from a
                        // player we've seen before. There are a few scenarios:
                        if (status == PlayerState.Connected)
                        {
                            // If the player is in PlayerState.Connected, it means that
                            // the connection init response got dropped on the
                            // way to the client. We have to resend it.
                            ProcessConnectionInit(receivedAddress, data, ld);
                        }
                        else
                        {
                            // Otherwise, he probably just lagged off or his
                            // client crashed. Ideally, we'd postpone this
                            // packet, initiate a logout procedure, and then
                            // process it. We can't do that right now, so drop
                            // the packet, initiate the logout, and hope that
                            // the client re-sends it soon. 
                            _playerData.KickPlayer(player);
                        }

                        return;
                    }

                    Interlocked.Exchange(ref conn.LastReceiveTimestamp, Stopwatch.GetTimestamp());
                    Interlocked.Add(ref conn.BytesReceived, (ulong)bytesReceived);
                    Interlocked.Increment(ref conn.PacketsReceived);
                    Interlocked.Add(ref _globalStats.BytesReceived, (ulong)bytesReceived);
                    Interlocked.Increment(ref _globalStats.PacketsReceived);

                    if (conn.Encryptor is not null)
                    {
                        bytesReceived = conn.Encryptor.Decrypt(player, _receiveBuffer, bytesReceived);

                        if (bytesReceived <= 0)
                        {
                            // bad crc, or something
                            _logManager.LogM(LogLevel.Malicious, nameof(Network), $"[pid={player.Id}] Failure decrypting packet.");
                            return;
                        }

                        data = _receiveBuffer.AsSpan(0, bytesReceived);

#if CFG_DUMP_RAW_PACKETS
                        DumpPk($"DECRYPTED GAME DATA: {bytesReceived} bytes", data);
#endif
                    }

                    ProcessBuffer(conn, data, NetReceiveFlags.None);
                }
                finally
                {
                    Interlocked.Decrement(ref conn.ProcessingHolds);
                }


                static bool IsConnectionInitPacket(ReadOnlySpan<byte> data)
                {
                    return data.Length >= 2
                        && data[0] == 0x00
                        && ((data[1] == 0x01) || (data[1] == 0x11));
                }

                bool ProcessConnectionInit(SocketAddress remoteAddress, ReadOnlySpan<byte> data, ListenData ld)
                {
                    _connectionInitLock.EnterReadLock();

                    try
                    {
                        foreach (ConnectionInitHandler handler in _connectionInitHandlers)
                        {
                            if (handler(remoteAddress, data, ld))
                                return true;
                        }
                    }
                    finally
                    {
                        _connectionInitLock.ExitReadLock();
                    }

                    _logManager.LogM(LogLevel.Info, nameof(Network), $"Got a connection init packet from {remoteAddress}, but no handler processed it.  Please verify that an encryption module is loaded or the {nameof(EncryptionNull)} module if no encryption is desired.");
                    return false;
                }
            }

            void HandlePingPacketReceived(ListenData ld)
            {
                if (ld is null)
                    return;

                SocketAddress receivedAddress = ld.PingSocket.AddressFamily == AddressFamily.InterNetworkV6 ? _receiveSocketAddressV6 : _receiveSocketAddressV4;

                int numBytes;

                try
                {
                    numBytes = ld.PingSocket.ReceiveFrom(_receiveBuffer, SocketFlags.None, receivedAddress);
                }
                catch (SocketException ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"SocketException with error code {ex.ErrorCode} when receiving from ping socket {ld.PingSocket.LocalEndPoint}. {ex}");
                    return;
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"Exception when receiving from ping socket {ld.PingSocket.LocalEndPoint}. {ex}");
                    return;
                }

                if (numBytes != 4 && numBytes != 8)
                    return;

                //
                // Refresh data (if needed)
                //

                if (_pingData.LastRefresh is null
                    || (DateTime.UtcNow - _pingData.LastRefresh) > _config.PingRefreshThreshold)
                {
                    uint globalTotal = 0;
                    uint globalPlaying = 0;

                    Span<byte> remainingArenaSummary = _pingData.ArenaSummaryBytes;

                    // Reset temporary counts for arenas.
                    foreach (PopulationStats stats in _pingData.ConnectAsPopulationStats.Values)
                    {
                        stats.TempTotal = stats.TempPlaying = 0;
                    }

                    IArenaManager? arenaManager = _broker.GetInterface<IArenaManager>();
                    if (arenaManager is not null)
                    {
                        try
                        {
                            arenaManager.Lock();

                            try
                            {
                                // Refresh stats.
                                arenaManager.GetPopulationSummary(out int total, out int playing);

                                // Get global stats.
                                globalTotal = (uint)total;
                                globalPlaying = (uint)playing;

                                // Get arena stats.
                                foreach (Arena arena in arenaManager.Arenas)
                                {
                                    arena.GetPlayerCounts(out int arenaTotal, out int arenaPlaying);

                                    // Arena summary for ping/info payload
                                    if (arena.Status == ArenaState.Running
                                        && !arena.IsPrivate
                                        && remainingArenaSummary.Length > StringUtils.DefaultEncoding.GetByteCount(arena.Name) + 1 + 2 + 2 + 1) // name + null-terminator + Int16 total + Int16 playing + enough space for a single nul to the series
                                    {
                                        // Name
                                        int bytesWritten = StringUtils.WriteNullTerminatedString(remainingArenaSummary, arena.Name);
                                        remainingArenaSummary = remainingArenaSummary[bytesWritten..];

                                        // Total
                                        BinaryPrimitives.WriteUInt16LittleEndian(remainingArenaSummary, arenaTotal > ushort.MaxValue ? ushort.MaxValue : (ushort)arenaTotal);
                                        remainingArenaSummary = remainingArenaSummary[2..];

                                        // Playing
                                        BinaryPrimitives.WriteUInt16LittleEndian(remainingArenaSummary, arenaPlaying > ushort.MaxValue ? ushort.MaxValue : (ushort)arenaPlaying);
                                        remainingArenaSummary = remainingArenaSummary[2..];
                                    }

                                    // Connect As
                                    if (_pingData.ConnectAsPopulationStats.TryGetValue(arena.BaseName, out PopulationStats? stats))
                                    {
                                        stats.TempTotal += (uint)arenaTotal;
                                        stats.TempPlaying += (uint)arenaPlaying;
                                    }
                                }
                            }
                            finally
                            {
                                arenaManager.Unlock();
                            }
                        }
                        finally
                        {
                            _broker.ReleaseInterface(ref arenaManager);
                        }
                    }

                    if (remainingArenaSummary.Length != _pingData.ArenaSummaryBytes.Length)
                    {
                        // A one-byte chunk with a zero-length name (that is, a single nul byte) indicates the end of the series.
                        remainingArenaSummary[0] = 0;
                        remainingArenaSummary = remainingArenaSummary[1..];
                    }

                    _pingData.ArenaSummaryLength = _pingData.ArenaSummaryBytes.Length - remainingArenaSummary.Length;

                    // Set arena stats.
                    foreach (PopulationStats stats in _pingData.ConnectAsPopulationStats.Values)
                    {
                        stats.SetStats(stats.TempTotal, stats.TempPlaying);
                    }

                    // Get peer stats.
                    uint peerTotal = 0;
                    IPeer? peer = _broker.GetInterface<IPeer>();
                    if (peer is not null)
                    {
                        try
                        {
                            peerTotal = (uint)peer.GetPopulationSummary();
                            // peer protocol does not provide a "playing" count
                        }
                        finally
                        {
                            _broker.ReleaseInterface(ref peer);
                        }
                    }

                    // Set global stats.
                    _pingData.Global.SetStats(globalTotal + peerTotal, globalPlaying);

                    // Note: LastRefresh is only accessed by this thread. So, no write lock needed.
                    _pingData.LastRefresh = DateTime.UtcNow;
                }

                //
                // Respond
                //

                if (numBytes == 4)
                {
                    // Regular "simple" ping protocol

                    // Reuse the buffer for sending.
                    Span<byte> data = _receiveBuffer.AsSpan(0, 8);

                    // Copy bytes received.
                    Span<byte> valueSpan = data[..4];
                    valueSpan.CopyTo(data.Slice(4, 4));

                    if (string.IsNullOrWhiteSpace(ld.ConnectAs)
                        || !_pingData.ConnectAsPopulationStats.TryGetValue(ld.ConnectAs, out PopulationStats? stats))
                    {
                        stats = _pingData.Global;
                    }

                    // # of clients
                    // Note: ASSS documentation says it's a UInt32, but it appears Continuum looks at only the first 2 bytes as an UInt16.
                    uint count;
                    stats.GetStats(out uint total, out uint playing);
                    if ((_config.SimplePingPopulationMode & PingPopulationMode.Total) != 0)
                    {
                        if ((_config.SimplePingPopulationMode & PingPopulationMode.Playing) != 0)
                        {
                            // Alternate between total and playing counts every 3 seconds.
                            // Note: ASSS uses the data received (which should be a timestamp from the client). Instead, this uses the server's tick count.
                            count = ServerTick.Now % 600 < 300 ? total : playing;
                        }
                        else
                        {
                            count = total;
                        }
                    }
                    else if ((_config.SimplePingPopulationMode & PingPopulationMode.Playing) != 0)
                    {
                        count = playing;
                    }
                    else
                    {
                        count = 0;
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(valueSpan, count);

                    try
                    {
                        int bytesSent = ld.PingSocket.SendTo(data, SocketFlags.None, receivedAddress);
                    }
                    catch (SocketException ex)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(Network), $"SocketException with error code {ex.ErrorCode} when sending to {receivedAddress} with ping socket {ld.PingSocket.LocalEndPoint}. {ex}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(Network), $"Exception when sending to {receivedAddress} with ping socket {ld.PingSocket.LocalEndPoint}. {ex}");
                        return;
                    }
                }
                else if (numBytes == 8)
                {
                    // ASSS ping/information protocol
                    Span<byte> optionsSpan = _receiveBuffer.AsSpan(4, 4);
                    PingOptionFlags optionsIn = (PingOptionFlags)BinaryPrimitives.ReadUInt32LittleEndian(optionsSpan);
                    PingOptionFlags optionsOut = PingOptionFlags.None;
                    Span<byte> remainingBuffer = _receiveBuffer.AsSpan(8);

                    // Global summary
                    if ((optionsIn & PingOptionFlags.GlobalSummary) != 0
                        && remainingBuffer.Length >= 8)
                    {
                        _pingData.Global.GetStats(out uint total, out uint playing);
                        BinaryPrimitives.WriteUInt32LittleEndian(remainingBuffer, total);
                        remainingBuffer = remainingBuffer[4..];
                        BinaryPrimitives.WriteUInt32LittleEndian(remainingBuffer, playing);
                        remainingBuffer = remainingBuffer[4..];
                        optionsOut |= PingOptionFlags.GlobalSummary;
                    }

                    // Arena summary
                    if ((optionsIn & PingOptionFlags.ArenaSummary) != 0
                        && _pingData.ArenaSummaryLength > 0
                        && remainingBuffer.Length >= _pingData.ArenaSummaryLength)
                    {
                        _pingData.ArenaSummaryBytes.AsSpan(0, _pingData.ArenaSummaryLength).CopyTo(remainingBuffer);
                        remainingBuffer = remainingBuffer[_pingData.ArenaSummaryLength..];
                        optionsOut |= PingOptionFlags.ArenaSummary;
                    }

                    // Fill in the outgoing options.
                    BinaryPrimitives.WriteUInt32LittleEndian(optionsSpan, (uint)optionsOut);

                    // Send the packet.
                    try
                    {
                        int bytesSent = ld.PingSocket.SendTo(_receiveBuffer.AsSpan(0, _receiveBuffer.Length - remainingBuffer.Length), SocketFlags.None, receivedAddress);
                    }
                    catch (SocketException ex)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(Network), $"SocketException with error code {ex.ErrorCode} when sending to {receivedAddress} with ping socket {ld.PingSocket.LocalEndPoint}. {ex}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogM(LogLevel.Error, nameof(Network), $"Exception when sending to {receivedAddress} with ping socket {ld.PingSocket.LocalEndPoint}. {ex}");
                        return;
                    }
                }

                Interlocked.Increment(ref _globalStats.PingsReceived);
            }

            void HandleClientPacketReceived()
            {
                int bytesReceived;
                SocketAddress receivedAddress = _clientSocket.AddressFamily == AddressFamily.InterNetworkV6 ? _receiveSocketAddressV6 : _receiveSocketAddressV4;

                try
                {
                    bytesReceived = _clientSocket.ReceiveFrom(_receiveBuffer, SocketFlags.None, receivedAddress);
                }
                catch (SocketException ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"SocketException with error code {ex.ErrorCode} when receiving from client socket {_clientSocket.LocalEndPoint}. {ex}");
                    return;
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"Exception when receiving from client socket {_clientSocket.LocalEndPoint}. {ex}");
                    return;
                }

                if (bytesReceived < 1)
                {
                    return;
                }

                Span<byte> data = _receiveBuffer.AsSpan(0, bytesReceived);

#if CFG_DUMP_RAW_PACKETS
                DumpPk($"RECV CLIENT DATA: {bytesReceived} bytes", data);
#endif

                bool found;
                ClientConnection? clientConnection;

                _clientConnectionsLock.EnterReadLock();

                try
                {
                    found = _clientConnections.TryGetValue(receivedAddress, out clientConnection);

                    if (found)
                    {
                        if (clientConnection!.Status >= ClientConnectionStatus.Disconnecting)
                            return;

                        Interlocked.Increment(ref clientConnection.ProcessingHolds);
                    }
                }
                finally
                {
                    _clientConnectionsLock.ExitReadLock();
                }

                if (!found)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(Network), $"Got data on the client port that was not from any known connection ({receivedAddress}).");
                    return;
                }

                try
                {
                    Interlocked.Exchange(ref clientConnection!.LastReceiveTimestamp, Stopwatch.GetTimestamp());
                    Interlocked.Add(ref clientConnection.BytesReceived, (ulong)bytesReceived);
                    Interlocked.Increment(ref clientConnection.PacketsReceived);
                    Interlocked.Add(ref _globalStats.BytesReceived, (ulong)bytesReceived);
                    Interlocked.Increment(ref _globalStats.PacketsReceived);

                    if (clientConnection.Encryptor is not null)
                    {
                        bytesReceived = clientConnection.Encryptor.Decrypt(clientConnection, _receiveBuffer, bytesReceived);

                        if (bytesReceived < 1)
                        {
                            _logManager.LogM(LogLevel.Malicious, nameof(Network), "(client connection) Failed to decrypt packet.");
                            return;
                        }

                        data = _receiveBuffer.AsSpan(0, bytesReceived);

#if CFG_DUMP_RAW_PACKETS
                        DumpPk($"DECRYPTED CLIENT DATA: {bytesReceived} bytes", data);
#endif
                    }

                    ProcessBuffer(clientConnection, data, NetReceiveFlags.None);
                }
                finally
                {
                    Interlocked.Decrement(ref clientConnection!.ProcessingHolds);
                }
            }
        }

        private void SendThread()
        {
            Span<byte> groupedPacketBuffer = stackalloc byte[Constants.MaxGroupedPacketLength];
            PacketGrouper packetGrouper = new(this, groupedPacketBuffer);

            List<Player> toKick = new(Constants.TargetPlayerCount);
            List<Player> toFree = new(Constants.TargetPlayerCount);
            List<ClientConnection> toDrop = [];

            while (_stopToken.IsCancellationRequested == false)
            {
                long start = Stopwatch.GetTimestamp();

                //
                // Players
                //

                // Send outgoing packets.
                _playerData.Lock();

                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Status >= PlayerState.Connected
                            && player.Status < PlayerState.TimeWait
                            && IsOurs(player))
                        {
                            if (!player.TryGetExtraData(_connKey, out PlayerConnection? conn))
                                continue;

                            if (conn.OutLock.TryEnter())
                            {
                                try
                                {
                                    SendOutgoing(conn, ref packetGrouper);
                                    SubmitRelStats(player);
                                }
                                finally
                                {
                                    conn.OutLock.Exit();
                                }
                            }

                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                // Process lagouts and disconnects
                _playerData.Lock();
                long now = Stopwatch.GetTimestamp();
                try
                {
                    foreach (Player player in _playerData.Players)
                    {
                        if (player.Status >= PlayerState.Connected
                            && IsOurs(player))
                        {
                            if (!player.TryGetExtraData(_connKey, out PlayerConnection? conn))
                                return;

                            if (ProcessLagout(player, conn, now))
                                toKick.Add(player);

                            if (ProcessDisconnect(player, conn, false))
                                toFree.Add(player);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                // Kick the players that lagged out.
                if (toKick.Count > 0)
                {
                    foreach (Player player in toKick)
                    {
                        _playerData.KickPlayer(player);
                    }

                    toKick.Clear();
                }

                // Free the players that have been disconnected.
                if (toFree.Count > 0)
                {
                    foreach (Player player in toFree)
                    {
                        _playerData.FreePlayer(player);
                    }

                    toFree.Clear();
                }

                //
                // Client connections
                //

                now = Stopwatch.GetTimestamp();

                _clientConnectionsLock.EnterUpgradeableReadLock();

                try
                {
                    foreach (ClientConnection clientConnection in _clientConnections.Values)
                    {
                        bool hitMaxRetries = false;
                        bool hitMaxOutlist = false;

                        // Send outgoing.
                        if (clientConnection.Status >= ClientConnectionStatus.Connected
                            && clientConnection.Status < ClientConnectionStatus.Disconnecting)
                        {
                            lock (clientConnection.OutLock)
                            {
                                SendOutgoing(clientConnection, ref packetGrouper);

                                hitMaxRetries = clientConnection.HitMaxRetries;
                                hitMaxOutlist = clientConnection.HitMaxOutlist;
                            }
                        }

                        // Check drop conditions (ordered to disconnect or lag out).
                        TimeSpan noDataLimit = Interlocked.CompareExchange(ref clientConnection.PacketsReceived, 0, 0) > 0 ? TimeSpan.FromSeconds(65) : TimeSpan.FromSeconds(10);

                        string? reason;
                        if (clientConnection.Status == ClientConnectionStatus.Disconnecting)
                            reason = "command";
                        else if (hitMaxRetries)
                            reason = "too many reliable retries";
                        else if (hitMaxOutlist)
                            reason = "too many outgoing packets";
                        else if (Stopwatch.GetElapsedTime(Interlocked.Read(ref clientConnection.LastReceiveTimestamp), now) > noDataLimit)
                            reason = "no data";
                        else
                            reason = null;

                        if (reason is not null)
                        {
                            if (TearDownConnection(clientConnection, false))
                            {
                                _logManager.LogM(LogLevel.Info, nameof(Network), $"Client connection dropped due to {reason}.");
                                toDrop.Add(clientConnection);
                            }
                        }
                    }

                    // Drop any connections that need to be dropped.
                    if (toDrop.Count > 0)
                    {
                        foreach (ClientConnection clientConnection in toDrop)
                        {
                            bool removed;

                            _clientConnectionsLock.EnterWriteLock();

                            try
                            {
                                removed = _clientConnections.Remove(clientConnection.RemoteAddress!);
                            }
                            finally
                            {
                                _clientConnectionsLock.ExitWriteLock();
                            }

                            if (removed)
                            {
                                ReadOnlySpan<byte> disconnectSpan = [0x00, 0x07];
                                SendRaw(clientConnection, disconnectSpan);
                            }

                            if (clientConnection.Encryptor is not null)
                            {
                                clientConnection.Encryptor.Void(clientConnection);
                                _broker.ReleaseInterface(ref clientConnection.Encryptor, clientConnection.EncryptorName);
                            }

                            clientConnection.Status = ClientConnectionStatus.Disconnected;
                        }
                    }
                }
                finally
                {
                    _clientConnectionsLock.ExitUpgradeableReadLock();
                }

                if (toDrop.Count > 0)
                {
                    foreach (ClientConnection clientConnection in toDrop)
                    {
                        // Tell the handler it's disconnected.
                        _mainloop.QueueMainWorkItem(_mainloopWork_ClientConnectionHandlerDisconnected, clientConnection);
                    }

                    toDrop.Clear();
                }

                if (_stopToken.IsCancellationRequested)
                    return;

                // Wait at least 1 tick from the start of the iteration, 1/100 second
                int msToWait = (10 - (int)Math.Floor(Stopwatch.GetElapsedTime(start).TotalMilliseconds));

#if CFG_LOG_SEND_THREAD_PERFORMANCE
                if (msToWait < 10)
                {
                    _logManager.LogM(LogLevel.Drivel, nameof(Network), $"SendThread wait {msToWait} ms.");
                }
#endif
                if (msToWait <= 0)
                {
                    // The processing took 10 ms or longer. Continue processing again immediately.
                    continue;
                }
                else
                {
                    if (_playerConnections.IsEmpty)
                    {
                        // No players, so no urgency.
                        Thread.Sleep(msToWait);
                    }
                    else
                    {
                        // There are players, so use a more granular method for waiting if needed.
                        switch (_config.SendThreadWaitOption)
                        {
                            case SendThreadWaitOption.BusyWait:
                                TimeSpan waitTimeSpan = TimeSpan.FromMilliseconds(msToWait);
                                while (Stopwatch.GetElapsedTime(start) < waitTimeSpan || _isCancellationRequested())
                                {
                                }
                                break;

                            case SendThreadWaitOption.SpinWait:
                                SpinWait.SpinUntil(_isCancellationRequested, msToWait);
                                break;

                            case SendThreadWaitOption.Sleep:
                            default:
                                Thread.Sleep(msToWait);
                                break;
                        }
                    }
                }
            }

            void SubmitRelStats(Player player)
            {
                if (player is null)
                    return;

                if (_lagCollect is null)
                    return;

                if (!player.TryGetExtraData(_connKey, out PlayerConnection? conn))
                    return;

                ReliableLagData rld = new()
                {
                    RelDups = Interlocked.Read(ref conn.RelDups),
                    ReliablePacketsReceived = (uint)Interlocked.CompareExchange(ref conn.ReliablePacketsReceived, 0, 0),
                    Retries = Interlocked.Read(ref conn.Retries),
                    ReliablePacketsSent = (uint)Interlocked.CompareExchange(ref conn.ReliablePacketsSent, 0, 0),
                };

                _lagCollect.RelStats(player, in rld);
            }

            bool ProcessLagout(Player player, PlayerConnection conn, long now)
            {
                ArgumentNullException.ThrowIfNull(player);
                ArgumentNullException.ThrowIfNull(conn);

                bool hitMaxRetries;
                bool hitMaxOutlist;

                lock (conn.OutLock)
                {
                    hitMaxRetries = conn.HitMaxRetries;
                    hitMaxOutlist = conn.HitMaxOutlist;
                }

                // Process lagouts
                if (player.WhenLoggedIn == PlayerState.Uninitialized // acts as flag to prevent dups
                    && player.Status < PlayerState.LeavingZone // don't kick them if they're already on the way out
                    && (hitMaxRetries || hitMaxOutlist || Stopwatch.GetElapsedTime(Interlocked.Read(ref conn.LastReceiveTimestamp), now) > _config.DropTimeout))
                {
                    string reason;
                    if (hitMaxRetries)
                        reason = "too many reliable retries";
                    else if (hitMaxOutlist)
                        reason = "too many outgoing packets";
                    else
                        reason = "no data";

                    // Send a disconnect chat message.
                    // This is sent unreliably since there's no guarantee everything in the outgoing reliable queue will get sent before the player's connection is disconnected.
                    const string disconnectMessageBegin = "You have been disconnected because of lag (";
                    const string disconnectMessageEnd = ").";
                    Span<char> message = stackalloc char[disconnectMessageBegin.Length + reason.Length + disconnectMessageEnd.Length];
                    if (message.TryWrite($"{disconnectMessageBegin}{reason}{disconnectMessageEnd}", out int charsWritten))
                    {
                        message = message[..charsWritten];

                        Span<byte> chatBytes = stackalloc byte[ChatPacket.GetPacketByteCount(message)];
                        ref ChatPacket chatPacket = ref MemoryMarshal.AsRef<ChatPacket>(chatBytes);
                        chatPacket.Type = (byte)S2CPacketType.Chat;
                        chatPacket.ChatType = (byte)ChatMessageType.SysopWarning;
                        chatPacket.Sound = (byte)ChatSound.None;
                        chatPacket.PlayerId = -1;
                        int length = ChatPacket.SetMessage(chatBytes, message);

                        SendRaw(conn, chatBytes[..length]);
                    }

                    _logManager.LogP(LogLevel.Info, nameof(Network), player, $"Kicked for {reason}.");
                    return true;
                }

                return false;
            }
        }

        private bool IsCancellationRequested()
        {
            return _stopToken.IsCancellationRequested;
        }

        private bool ProcessDisconnect(Player player, PlayerConnection playerConnection, bool force)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(playerConnection);

            Debug.Assert(player == playerConnection.Player);
            Debug.Assert(!force || (force && _stopCancellationTokenSource.IsCancellationRequested));

            if (!force && player.Status != PlayerState.TimeWait)
            {
                // Not in a state to disconnect.
                return false;
            }

            // The player should be disconnected. Try to tear down the connection.
            if (!TearDownConnection(playerConnection, force))
            {
                // The connection is still in use. We'll try again on the next go around.
                return false;
            }

            if (_playerConnections.TryRemove(playerConnection.RemoteAddress!, out _))
            {
                // Send disconnection packet.
                Span<byte> disconnectSpan = [0x00, 0x07];
                SendRaw(playerConnection, disconnectSpan);
            }

            // Cleanup encryptor.
            if (playerConnection.Encryptor is not null)
            {
                playerConnection.Encryptor.Void(player);
                _broker.ReleaseInterface(ref playerConnection.Encryptor, playerConnection.EncryptorName);
            }

            _logManager.LogP(LogLevel.Info, nameof(Network), player, "Disconnected.");
            return true;
        }

        private bool TearDownConnection(ConnData conn, bool force)
        {
            Debug.Assert(!force || (force && _stopCancellationTokenSource.IsCancellationRequested));

            // Sized sends
            if (force)
            {
                lock (conn.SizedSendLock)
                {
                    LinkedListNode<ISizedSendData>? node = conn.SizedSends.First;
                    while (node is not null)
                    {
                        LinkedListNode<ISizedSendData>? next = node.Next;

                        ref ISizedSendData sizedSend = ref node.ValueRef;
                        sizedSend.RequestData([]);
                        sizedSend.Return();
                        conn.SizedSends.Remove(node);
                        _sizedSendDataNodePool.Return(node);

                        node = next;
                    }
                }
            }
            else
            {
                bool hasSizedSend;
                bool cancelledSizeSend = false;

                lock (conn.SizedSendLock)
                {
                    LinkedListNode<ISizedSendData>? node = conn.SizedSends.First;
                    hasSizedSend = node is not null;

                    // Make sure that all sized sends are cancelled.
                    while (node is not null)
                    {
                        LinkedListNode<ISizedSendData>? next = node.Next;

                        ISizedSendData ssd = node.Value;
                        if (!ssd.IsCancellationRequested)
                        {
                            ssd.Cancel(false);
                            cancelledSizeSend = true;
                        }

                        node = node.Next;
                    }
                }

                if (cancelledSizeSend)
                {
                    // Make sure the connection is in the sized send queue to be processed.
                    _sizedSendQueue.TryEnqueue(conn);
                }

                if (hasSizedSend)
                {
                    // Wait for the SizedSendThread to process the sized send(s).
                    // On a later iteration the sized sends will be gone.
                    return false;
                }
            }

            // Clear all our buffers
            // Note: This also clears the outgoing reliable queue (unsent and outlist).
            // If any had async reliable callbacks, they'll get queued, and we'll wait until they're complete (notice the next check we do on ConnData.ProcessingHolds).
            ClearBuffers(conn);

            if (!force && Interlocked.CompareExchange(ref conn.ProcessingHolds, 0, 0) != 0)
            {
                // There is ongoing processing for the connection on another thread or queued async work that needs to complete.
                // Wait for the processing to finish. On a later iteration it should be over.
                return false;
            }

            if (conn.BandwidthLimiter is not null)
            {
                (conn.BandwidthLimiterProvider ?? _bandwidthLimiterProvider).Free(conn.BandwidthLimiter);
                conn.BandwidthLimiter = null;
            }

            if (conn.BandwidthLimiterProvider is not null)
            {
                _broker.ReleaseInterface(ref conn.BandwidthLimiterProvider, conn.BandwidthLimiterProviderName);
                conn.BandwidthLimiterProviderName = null;
            }

            // NOTE: The encryptor can't be removed yet since the disconnect packet still needs to be sent.

            return true;


            void ClearBuffers(ConnData conn)
            {
                if (conn is null)
                    return;

                if (conn is PlayerConnection playerConnection)
                {
                    Player? player = playerConnection.Player;
                    if (player is not null)
                    {
                        lock (conn.BigLock)
                        {
                            EndSizedReceive(player, false);
                        }
                    }
                }

                lock (conn.OutLock)
                {
                    // unsent reliable outgoing queue
                    ClearOutgoingQueue(conn.UnsentRelOutList);

                    // regular outgoing queues
                    for (int i = 0; i < conn.OutList.Length; i++)
                    {
                        ClearOutgoingQueue(conn.OutList[i]);
                    }
                }

                // There should be no sized sends when this method is called.
                // The SizedSendThread should have already cleaned it up, since we waited.
#if DEBUG
                lock (conn.SizedSendLock)
                {
                    Debug.Assert(conn.SizedSends.Count == 0);
                }
#endif

                // now clear out the connection's incoming rel buffer
                lock (conn.ReliableLock)
                {
                    conn.ReliableBuffer.Reset();
                }

                // and remove from reliable signaling queue
                if (_relQueue.Remove(conn))
                {
                    Interlocked.Decrement(ref conn.ProcessingHolds);
                }

                void ClearOutgoingQueue(LinkedList<SubspaceBuffer> outlist)
                {
                    LinkedListNode<SubspaceBuffer>? nextNode;
                    for (LinkedListNode<SubspaceBuffer>? node = outlist.First; node is not null; node = nextNode)
                    {
                        nextNode = node.Next;

                        SubspaceBuffer b = node.Value;
                        if (b.CallbackInvoker is not null)
                        {
                            ExecuteReliableCallback(b.CallbackInvoker, conn, false);
                            b.CallbackInvoker = null;
                        }

                        outlist.Remove(node);
                        _bufferNodePool.Return(node);
                        b.Dispose();
                    }
                }
            }
        }

        private void SendOutgoing(ConnData conn, ref PacketGrouper packetGrouper, BandwidthPriority? priorityFilter = null)
        {
            long now = Stopwatch.GetTimestamp();
            PlayerConnection? playerConnection = conn as PlayerConnection;

            if (playerConnection is not null // game protocol only
                && playerConnection.Player is not null
                && playerConnection.Player.Status == PlayerState.Playing
                && Stopwatch.GetElapsedTime(Interlocked.Read(ref conn.LastSendTimestamp), now) > _config.PlayerKeepAliveThreshold)
            {
                // Check if there is any pending outgoing data.
                bool hasPendingOutgoing = false;

                if (conn.UnsentRelOutList.Count > 0)
                {
                    hasPendingOutgoing = true;
                }
                else
                {
                    for (int pri = conn.OutList.Length - 1; pri >= 0; pri--)
                    {
                        if (conn.OutList[pri].Count > 0)
                        {
                            hasPendingOutgoing = true;
                            break;
                        }
                    }
                }

                if (!hasPendingOutgoing)
                {
                    // Queue a keep alive packet to be sent the player.
                    SendToOne(conn, [(byte)S2CPacketType.KeepAlive], NetSendFlags.Unreliable);
                }
            }

            // use an estimate of the average round-trip time to figure out when to resend a packet
            uint timeout = Math.Clamp((uint)(conn.AverageRoundTripTime + (4 * conn.AverageRoundTripDeviation)), 250, 2000);

            // update the bandwidth limiter's counters
            conn.BandwidthLimiter!.Iter(now);

            int canSend = conn.BandwidthLimiter.GetSendWindowSize();
            int retries = 0;
            int outlistlen = 0;

            packetGrouper.Initialize();

            // process the highest priority first
            for (int pri = conn.OutList.Length - 1; pri >= 0; pri--)
            {
                if (priorityFilter is not null && pri != (int)priorityFilter.Value)
                    continue;

                LinkedList<SubspaceBuffer> outlist = conn.OutList[pri];
                bool isReliable = (pri == (int)BandwidthPriority.Reliable);

                if (isReliable)
                {
                    // move packets from UnsentRelOutList to outlist, grouped if possible
                    while (conn.UnsentRelOutList.Count > 0)
                    {
                        if (outlist.Count > 0)
                        {
                            // The reliable sending/pending queue has at least one packet,
                            // Get the first one (lowest sequence number) and use that number to determine if there's room to add more.
                            ref readonly ReliableHeader min = ref MemoryMarshal.AsRef<ReliableHeader>(outlist.First!.Value.Bytes);
                            if ((conn.SeqNumOut - min.SeqNum) >= canSend)
                            {
                                break;
                            }
                        }

                        LinkedListNode<SubspaceBuffer> n1 = conn.UnsentRelOutList.First!;
                        SubspaceBuffer b1 = conn.UnsentRelOutList.First!.Value;
#if !DISABLE_GROUPED_SEND
                        if (b1.NumBytes <= Constants.MaxGroupedPacketItemLength && conn.UnsentRelOutList.Count > 1)
                        {
                            // The 1st packet can fit into a grouped packet and there's at least one more packet available, check if it's possible to group them together
                            SubspaceBuffer b2 = n1.Next!.Value;

                            // Note: It is probably more beneficial to group as many as possible, even if it does go over 255 bytes. However, made it configurable.
                            int maxRelGroupedPacketLength = _config.LimitReliableGroupingSize
                                ? Constants.MaxGroupedPacketItemLength // limit reliable packet grouping up to 255 bytes so that the result can still fit into another grouped packet later on
                                : Constants.MaxGroupedPacketLength; // (default) group as many as is possible to fit into a fully sized grouped packet

                            if (b2.NumBytes <= Constants.MaxGroupedPacketItemLength // the 2nd packet can fit into a grouped packet too
                                && (ReliableHeader.Length + 2 + 1 + b1.NumBytes + 1 + b2.NumBytes) <= maxRelGroupedPacketLength) // can fit together in a reliable packet containing a grouped packet containing both packets
                            {
                                // We know we can group at least the first 2
                                SubspaceBuffer groupedBuffer = _bufferPool.Get();
                                groupedBuffer.Conn = conn;
                                groupedBuffer.SendFlags = b1.SendFlags; // taking the flags from the first packet, though doesn't really matter since we already know it's reliable
                                groupedBuffer.LastTryTimestamp = null;
                                groupedBuffer.Tries = 0;

                                ref ReliableHeader groupedRelHeader = ref MemoryMarshal.AsRef<ReliableHeader>(groupedBuffer.Bytes.AsSpan(0, ReliableHeader.Length));
                                groupedRelHeader = new(conn.SeqNumOut++);

                                // Group up as many as possible
                                PacketGrouper relGrouper = new(this, groupedBuffer.Bytes.AsSpan(ReliableHeader.Length, maxRelGroupedPacketLength - ReliableHeader.Length));
                                LinkedListNode<SubspaceBuffer>? node = conn.UnsentRelOutList.First;
                                while (node is not null)
                                {
                                    LinkedListNode<SubspaceBuffer>? next = node.Next;
                                    SubspaceBuffer toAppend = node.Value;

                                    if (!relGrouper.TryAppend(new ReadOnlySpan<byte>(toAppend.Bytes, 0, toAppend.NumBytes)))
                                    {
                                        break;
                                    }

                                    if (toAppend.CallbackInvoker is not null)
                                    {
                                        if (groupedBuffer.CallbackInvoker is null)
                                        {
                                            groupedBuffer.CallbackInvoker = toAppend.CallbackInvoker;
                                        }
                                        else
                                        {
                                            IReliableCallbackInvoker invoker = groupedBuffer.CallbackInvoker;
                                            while (invoker.Next is not null)
                                                invoker = invoker.Next;

                                            invoker.Next = toAppend.CallbackInvoker;
                                        }

                                        toAppend.CallbackInvoker = null;
                                    }

                                    conn.UnsentRelOutList.Remove(node);
                                    _bufferNodePool.Return(node);
                                    toAppend.Dispose();

                                    node = next;
                                }

                                Debug.Assert(relGrouper.Count >= 2, $"A minimum of 2 packets should have been grouped, but the count was {relGrouper.Count}.");

                                groupedBuffer.NumBytes = ReliableHeader.Length + relGrouper.NumBytes;

                                if (relGrouper.Count > 0)
                                {
                                    Interlocked.Increment(ref _globalStats.RelGroupedStats[Math.Min(relGrouper.Count - 1, _globalStats.RelGroupedStats.Length - 1)]);
                                }

                                LinkedListNode<SubspaceBuffer> groupedNode = _bufferNodePool.Get();
                                groupedNode.Value = groupedBuffer;
                                outlist.AddLast(groupedNode);

                                Interlocked.Increment(ref conn.ReliablePacketsSent);
                                continue;
                            }
                        }
#endif
                        //
                        // Couldn't group the packet, just add it as an regular individual reliable packet.
                        //

                        // Move the data back, so that we can prepend it with the reliable header.
                        for (int i = b1.NumBytes - 1; i >= 0; i--)
                        {
                            b1.Bytes[i + ReliableHeader.Length] = b1.Bytes[i];
                        }

                        // Write in the reliable header.
                        ref ReliableHeader header = ref MemoryMarshal.AsRef<ReliableHeader>(b1.Bytes.AsSpan(0, ReliableHeader.Length));
                        header = new(conn.SeqNumOut++);

                        b1.NumBytes += ReliableHeader.Length;
                        b1.LastTryTimestamp = null;
                        b1.Tries = 0;

                        // Move the node from the unsent queue to the sending/pending queue.
                        conn.UnsentRelOutList.Remove(n1);
                        outlist.AddLast(n1);

                        Interlocked.Increment(ref conn.ReliablePacketsSent);
                        Interlocked.Increment(ref _globalStats.RelGroupedStats[0]);
                    }

                    // include the "unsent" packets in the outgoing count too
                    outlistlen += conn.UnsentRelOutList.Count;
                }

                LinkedListNode<SubspaceBuffer>? nextNode;
                for (LinkedListNode<SubspaceBuffer>? node = outlist.First; node is not null; node = nextNode)
                {
                    nextNode = node.Next;

                    SubspaceBuffer buf = node.Value;
                    outlistlen++;

                    // check some invariants
                    ref readonly ReliableHeader rp = ref MemoryMarshal.AsRef<ReliableHeader>(buf.Bytes);

                    if (rp.T1 == 0x00 && rp.T2 == 0x03)
                        Debug.Assert(pri == (int)BandwidthPriority.Reliable);
                    else if (rp.T1 == 0x00 && rp.T2 == 0x04)
                        Debug.Assert(pri == (int)BandwidthPriority.Ack);
                    else
                        Debug.Assert((pri != (int)BandwidthPriority.Reliable) && (pri != (int)BandwidthPriority.Ack));

                    if (isReliable)
                    {
                        // Check if it's time to send this yet (use linearly increasing timeouts).
                        if ((buf.Tries != 0) && (Stopwatch.GetElapsedTime(buf.LastTryTimestamp!.Value, now).TotalMilliseconds <= (timeout * buf.Tries)))
                        {
                            // It's been sent, but it's not time to retry yet.
                            continue;
                        }

                        if (buf.Tries > _config.MaxRetries)
                        {
                            // Too many tries. Kick the player.
                            conn.HitMaxRetries = true;
                            return;
                        }
                    }

                    // Batch position packets
                    if (playerConnection is not null) // game protocol only
                    {
                        // Only certain types of position packets can be batched.
                        S2CPacketType packetType = (S2CPacketType)buf.Bytes[0];
                        if (packetType == S2CPacketType.BatchedSmallPosition || packetType == S2CPacketType.BatchedLargePosition)
                        {
                            int itemLength = packetType == S2CPacketType.BatchedSmallPosition ? SmallPosition.Length : LargePosition.Length;

                            if ((buf.NumBytes - 1) % itemLength == 0)
                            {
                                // The data is a position packet that can be batched together (1 byte for the packet type, followed by 1 or more position items).
                                //
                                // Also, notice that the bandwidth checks done in this section do not modify the bandwidth stats (modify = false on the method calls).
                                // It does this to guarantee that there will be enough bandwidth available when the packet is sent.

                                Span<byte> remainingBytes;

                                int grouperRemainingLength = Math.Min(
                                    packetGrouper.RemainingLength, 
                                    1 + Constants.MaxGroupedPacketItemLength); // +1 for the byte that tells the grouped item's length

                                // To batch the position packet, we need to know whether we're trying to stuff it into an existing grouped packet
                                // or create a full sized packet to send separately.
                                if (packetGrouper.Count > 1 
                                    && grouperRemainingLength - 1 >= buf.NumBytes) // data will fit into the grouper, -1 for the byte that tells the grouped item's length
                                {
                                    if (conn.BandwidthLimiter.Check(1 + buf.NumBytes, (BandwidthPriority)pri, false))
                                    {
                                        // We'll try to create a batch that fits into the grouped packet.
                                        remainingBytes = buf.Bytes.AsSpan(buf.NumBytes, grouperRemainingLength - buf.NumBytes);
                                    }
                                    else
                                    {
                                        // No more bandwidth left to send this data.
                                        remainingBytes = [];
                                    }
                                }
                                else
                                {
                                    if (conn.BandwidthLimiter.Check(_config.PerPacketOverhead + buf.NumBytes, (BandwidthPriority)pri, false))
                                    {
                                        // We'll try to create a batch that fits into a full sized packet.
                                        remainingBytes = buf.Bytes.AsSpan(buf.NumBytes, Constants.MaxPacket - buf.NumBytes);
                                    }
                                    else
                                    {
                                        // No more bandwidth left to send this data.
                                        remainingBytes = [];
                                    }
                                }

                                // Search for additional position packets of the same type that can be combined with the current one.
                                LinkedListNode<SubspaceBuffer>? posNode = node.Next;
                                while (posNode is not null
                                    && remainingBytes.Length >= itemLength // there's room for another item
                                    && conn.BandwidthLimiter.Check(buf.NumBytes + itemLength, (BandwidthPriority)pri, false)) // there's enough bandwidth for another item
                                {
                                    LinkedListNode<SubspaceBuffer>? nextPosNode = posNode.Next;

                                    SubspaceBuffer posBuffer = posNode.Value;
                                    if (posBuffer.Bytes[0] == (byte)packetType // same packet type as the initial packet we're trying to add to
                                        && (posBuffer.NumBytes - 1) % itemLength == 0)
                                    {
                                        Span<byte> posData = posBuffer.Bytes.AsSpan(1, posBuffer.NumBytes - 1);
                                        if (remainingBytes.Length >= posData.Length // enough room to fit the data
                                            && conn.BandwidthLimiter.Check(buf.NumBytes + posData.Length, (BandwidthPriority)pri, false))
                                        {
                                            // Add it into the batch.
                                            posData.CopyTo(remainingBytes);
                                            remainingBytes = remainingBytes[posData.Length..];
                                            buf.NumBytes += posData.Length;

                                            if (nextNode == posNode)
                                            {
                                                // The change affects the next node to process after the one we just combined data into.
                                                nextNode = nextPosNode;
                                            }

                                            // Remove the node that we just combined the data from.
                                            outlist.Remove(posNode);
                                            _bufferNodePool.Return(posNode);
                                            posBuffer.Dispose();
                                        }
                                    }

                                    posNode = nextPosNode;
                                }
                            }
                        }
                    }

                    // At this point, there's only one more check to determine if we're sending this packet now: bandwidth limiting.

                    int checkBytes = buf.NumBytes;
                    if (buf.NumBytes > Constants.MaxGroupedPacketItemLength)
                        checkBytes += _config.PerPacketOverhead; // Can't be grouped, so definitely will be sent in its own datagram
                    else if (packetGrouper.Count == 0 || !packetGrouper.CheckAppend(buf.NumBytes))
                        checkBytes += _config.PerPacketOverhead + 2 + 1; // Start of a new grouped packet. So, include an overhead of: IP+UDP header + grouped packet header + grouped packet item header
                    else
                        checkBytes += 1; // Will be appended into a grouped packet (though, not the first in it). So, only include the overhead of the grouped packet item header.

                    // Note for the above checkBytes calculation:
                    // There is still a chance that at the end, there's only 1 packet remaining to be sent in the packetGrouper.
                    // In which case, when it gets flushed, it will send the individual packet, not grouped.
                    // This means we'd have told the bandwidth limiter 3 bytes more than we actually send, but that's negligible.

                    if (!conn.BandwidthLimiter.Check(checkBytes, (BandwidthPriority)pri, true))
                    {
                        // try dropping it, if we can
                        if ((buf.SendFlags & NetSendFlags.Droppable) != 0)
                        {
                            Debug.Assert(pri < (int)BandwidthPriority.Reliable);
                            outlist.Remove(node);
                            _bufferNodePool.Return(node);
                            buf.Dispose();
                            Interlocked.Increment(ref conn.PacketsDropped);
                            outlistlen--;
                        }

                        // but in either case, skip it
                        continue;
                    }

                    if (isReliable)
                    {
                        if (buf.Tries > 0)
                        {
                            // This is a retry, not an initial send.
                            // Keep record of it for lag stats and also reduce the bandwidth limit.
                            retries++;
                            conn.BandwidthLimiter.AdjustForRetry();
                        }

                        buf.LastTryTimestamp = Stopwatch.GetTimestamp();
                        buf.Tries++;
                    }

                    // This sends it or adds it to a pending grouped packet.
                    packetGrouper.Send(buf, conn);

                    // if we just sent an unreliable packet, free it so we don't send it again
                    if (!isReliable)
                    {
                        outlist.Remove(node);
                        _bufferNodePool.Return(node);
                        buf.Dispose();
                        outlistlen--;
                    }
                }
            }

            // Flush the pending grouped packet (send anything that it contains).
            packetGrouper.Flush(conn);

            Interlocked.Add(ref conn.Retries, (ulong)retries);

            if (outlistlen > _config.MaxOutlistSize)
                conn.HitMaxOutlist = true;
        }

        private void RelThread()
        {
            WaitHandle[] waitHandles = [_relQueue.ReadyEvent, _stopToken.WaitHandle];

            while (!_stopToken.IsCancellationRequested)
            {
                switch (WaitHandle.WaitAny(waitHandles))
                {
                    case 0:
                        ProcessReliableQueue();
                        break;

                    case 1:
                        // We've been told to stop.
                        return;
                }
            }

            void ProcessReliableQueue()
            {
                while (!_stopToken.IsCancellationRequested)
                {
                    // Get the next connection that has packets to process.
                    if (!_relQueue.TryDequeue(out ConnData? conn))
                    {
                        // No pending work.
                        break;
                    }

                    try
                    {
                        if (conn is PlayerConnection playerConnection)
                        {
                            Player? player = playerConnection.Player;
                            if (player is null)
                                continue;

                            _playerData.Lock();
                            try
                            {
                                if (player.Status >= PlayerState.LeavingZone)
                                    continue;
                            }
                            finally
                            {
                                _playerData.Unlock();
                            }
                        }
                        else if (conn is ClientConnection clientConnection)
                        {
                            _clientConnectionsLock.EnterReadLock();
                            try
                            {
                                if (clientConnection.Status >= ClientConnectionStatus.Disconnecting)
                                    continue;
                            }
                            finally
                            {
                                _clientConnectionsLock.ExitReadLock();
                            }
                        }

                        if (!conn.ReliableProcessingLock.TryEnter())
                        {
                            // Another RelThread is already processing the connection.
                            continue;
                        }

                        try
                        {
                            // Only process a limited # of packets so that one connection doesn't hog all the processing time.
                            int limit = conn.ReliableBuffer.Capacity; // Never modified, so ok to read outside of the ReliableLock.
                            int processedCount = 0;

                            do
                            {
                                SubspaceBuffer? buffer;

                                // Get the next buffer to process.
                                lock (conn.ReliableLock)
                                {
                                    if (!conn.ReliableBuffer.TryGetNext(out _, out buffer))
                                    {
                                        // Nothing left to process.
                                        break;
                                    }
                                }

                                // Process it.
                                using (buffer)
                                {
                                    ProcessBuffer(conn, new Span<byte>(buffer.Bytes, ReliableHeader.Length, buffer.NumBytes - ReliableHeader.Length), buffer.ReceiveFlags);
                                }
                            }
                            while (++processedCount <= limit);
                        }
                        finally
                        {
                            conn.ReliableProcessingLock.Exit();
                        }

                        // If there is more work, make sure the connection gets processed again.
                        bool requeue = false;

                        lock (conn.ReliableLock)
                        {
                            if (conn.ReliableBuffer.HasNext())
                            {
                                requeue = true;
                            }
                        }

                        if (requeue)
                        {
                            Interlocked.Increment(ref conn.ProcessingHolds);
                            if (!_relQueue.TryEnqueue(conn))
                            {
                                Interlocked.Decrement(ref conn.ProcessingHolds);
                            }
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref conn.ProcessingHolds);
                    }
                }
            }
        }

        /// <summary>
        /// Thread that manages sending of sized data to connections.
        /// </summary>
        /// <remarks>
        /// <para>
        /// In the "Core" protocol (Subspace's transport layer), "sized data" is a mechanism for transferring data that is too large to fit into a single packet.
        /// The data is transferred in such a way that the receiver knows how much data to expect, hence why it's called "sized data".
        /// When you see a downloading progress bar in the client, it's using sized data to transfer. For example, when downloading map files (lvl and lvz).
        /// 
        /// The concept behind sized data is simple: split the data into chunks so that each chunk fits into a packet, and include the size.
        /// 
        /// Sized data packets are always sent within reliable packets to maintain ordering.
        /// The first 2 bytes in a sized packet are: 0x00 0x0A
        /// The 0x00 indicates it's a "core" packet, and the 0x0A tells it's a sized data packet.
        /// The next 4 bytes are the total size of the transfer, followed by the chunk of data.
        /// </para>
        /// <para>
        /// This thread queues data to be sent for sized sends in progress.
        /// Also, it cleans up sized sends that have been cancelled.
        /// </para>
        /// <para>
        /// Technically, sized data is only sent to player connections. 
        /// However, this logic does not assume player connections only. 
        /// It could work for client connections as well, if there was ever a need.
        /// </para>
        /// </remarks>
        private void SizedSendThread()
        {
            WaitHandle[] waitHandles = [_sizedSendQueue.ReadyEvent, _stopToken.WaitHandle];

            while (!_stopToken.IsCancellationRequested)
            {
                switch (WaitHandle.WaitAny(waitHandles))
                {
                    case 0:
                        ProcessSizedSendQueue();
                        break;

                    case 1:
                        // We've been told to stop.
                        return;
                }
            }


            void ProcessSizedSendQueue()
            {
                // Process until there are no items left in the queue or we're told to stop.
                while (!_stopToken.IsCancellationRequested)
                {
                    // Try to dequeue.
                    if (!_sizedSendQueue.TryDequeue(out ConnData? conn))
                    {
                        // No work to do.
                        break;
                    }

                    if (ProcessSizedSends(conn) && _config.SizedSendOutgoing)
                    {
                        // At least one sized packet was buffered to be sent.
                        // Try to send the sized data (in the reliable outgoing queue) immediately rather than wait for the SendThread to do it.
                        // This doesn't guarantee the queued sized data will immediately sent (e.g. bandwidth limits), but it is given a chance.
                        SendOutgoingReliable(conn);

                        // TODO: It would be even better if we could signal the SendThread to process it instead, but that would probably require changing SendThread to use a producer-consumer queue.
                    }
                }
            }


            bool ProcessSizedSends(ConnData conn)
            {
                ArgumentNullException.ThrowIfNull(conn);

                // Note on thread synchronization:
                //
                // In ISizedSendData, only the cancellation flags (IsCancellationRequested and IsCancellationRequestedByConnection) are changed by other threads.
                // Everything else in ISizedSendData is solely accessed by this thread.
                // Therefore, ConnData.SizedSendLock is held when accessing the ConnData.SizedSend collection or an ISizedSendData object's cancellation flag.
                //
                // The global player data read lock is taken to access the Player.Status and held to prevent the status from changing while processing.

                PlayerConnection? playerConnection = conn as PlayerConnection;
                ClientConnection? clientConnection = conn as ClientConnection;
                Player? player = null;

                if (playerConnection is not null)
                {
                    player = playerConnection.Player;
                    if (player is null)
                        return false;
                }

                bool queuedData = false;

                while (true)
                {
                    LinkedListNode<ISizedSendData>? sizedSendNode;
                    ISizedSendData sizedSend;
                    bool cancelled;

                    lock (conn.SizedSendLock)
                    {
                        sizedSendNode = conn.SizedSends.First;
                        if (sizedSendNode is null)
                        {
                            // Nothing left to process.
                            break;
                        }

                        sizedSend = sizedSendNode.Value;
                        cancelled = sizedSend.IsCancellationRequested;
                    }

                    if (!cancelled && player is not null)
                    {
                        // Check if the player's connection is being disconnected.
                        _playerData.Lock();

                        try
                        {
                            if (player.Status == PlayerState.TimeWait)
                                cancelled = true;
                        }
                        finally
                        {
                            _playerData.Unlock();
                        }
                    }

                    if (!cancelled)
                    {
                        // Check if there is room for queuing data.
                        if (Interlocked.CompareExchange(ref conn.SizedSendQueuedCount, 0, 0) >= _config.SizedQueueThreshold)
                            break;

                        //
                        // Request data
                        //

                        // Determine how much data to request.
                        int requestAtOnce = _config.SizedQueuePackets * Constants.ChunkSize;
                        int needed = int.Min(requestAtOnce, sizedSend.Remaining);

                        if (needed > 0)
                        {
                            // Prepare the header.
                            Span<byte> headerSpan = [0x00, 0x0A, 0, 0, 0, 0];
                            BinaryPrimitives.WriteInt32LittleEndian(headerSpan[2..], sizedSend.TotalLength);

                            // Get a buffer to store the data in.
                            int lengthWithHeader = headerSpan.Length + needed;
                            byte[] buffer = ArrayPool<byte>.Shared.Rent(lengthWithHeader);
                            try
                            {
                                // Get the data
                                // This is purposely outside of any locks since requesting data may include file I/O which can block,
                                // and we don't want to block player status changes (global player data lock)
                                // or the SendThread from sending data (ConnData.olmtx).
                                Span<byte> bufferSpan = buffer.AsSpan(0, lengthWithHeader);
                                sizedSend.RequestData(bufferSpan[headerSpan.Length..]);

                                // Lock (connection status)
                                if (playerConnection is not null)
                                {
                                    _playerData.Lock();
                                }
                                else if (clientConnection is not null)
                                {
                                    _clientConnectionsLock.EnterReadLock();
                                }

                                try
                                {
                                    lock (conn.SizedSendLock)
                                    {
                                        // Now that we reacquired the lock, the sized send should still be the current one since only this thread removes items.
                                        Debug.Assert(sizedSend == conn.SizedSends.First?.Value);

                                        // Cancel out if the sized send was cancelled while we were requesting data OR if the connection is being disconnected.
                                        cancelled = sizedSend.IsCancellationRequested
                                            || (player is not null && player.Status == PlayerState.TimeWait)
                                            || (clientConnection is not null && clientConnection.Status >= ClientConnectionStatus.Disconnecting);

                                        if (!cancelled)
                                        {
                                            //
                                            // Queue data
                                            //

                                            // Break the data into sized send (0x0A) packets and queue them up.
                                            while (bufferSpan.Length > headerSpan.Length)
                                            {
                                                Span<byte> packetSpan = bufferSpan[..int.Min(bufferSpan.Length, headerSpan.Length + Constants.ChunkSize)];

                                                // Write the header in front of the data.
                                                headerSpan.CopyTo(packetSpan);

                                                // We want to get a callback when we receive an ACK back.
                                                ReliableConnectionCallbackInvoker invoker = _reliableConnectionCallbackInvokerPool.Get();
                                                invoker.SetCallback(_sizedSendChunkCompleted, ReliableCallbackExecutionOption.Synchronous);

                                                Interlocked.Increment(ref conn.SizedSendQueuedCount);
                                                SendOrBufferPacket(conn, packetSpan, NetSendFlags.PriorityN1 | NetSendFlags.Reliable, invoker);
                                                queuedData = true;

                                                bufferSpan = bufferSpan[(packetSpan.Length - headerSpan.Length)..];
                                            }
                                        }
                                    }
                                }
                                finally
                                {
                                    // Unlock (connection status)
                                    if (playerConnection is not null)
                                    {
                                        _playerData.Unlock();
                                    }
                                    else if (clientConnection is not null)
                                    {
                                        _clientConnectionsLock.ExitReadLock();
                                    }
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                    }

                    if (cancelled || sizedSend.Remaining <= 0)
                    {
                        // The sized send is complete, either because it was cancelled or all the data was queued.

                        // Notify the sender that the transfer is complete, allowing it to perform cleanup.
                        // This is purposely done without holding onto any locks since the call may include blocking I/O.
                        sizedSend.RequestData([]);

                        bool sendCancellationAck = false;

                        lock (conn.SizedSendLock)
                        {
                            sendCancellationAck = cancelled && sizedSend.IsCancellationRequestedByConnection;

                            // Remove the sized send.
                            conn.SizedSends.Remove(sizedSendNode);
                            _sizedSendDataNodePool.Return(sizedSendNode);
                            sizedSend.Return();

                            if (conn.SizedSends.Count == 0)
                            {
                                // No more sized sends for the connection.

                                // This is purposely done while continuing to hold the ConnData.SizedSendLock,
                                // so that there will be no conflict if a sized send is being simultaneously added.
                                _sizedSendQueue.Remove(conn);
                            }
                        }

                        if (sendCancellationAck)
                        {
                            // The sized send was cancelled because it was requested by the connection (0x00 0x0B).
                            // This means we are supposed to respond with a sized cancellation ACK (0x00 0x0C),

                            if (playerConnection is not null)
                            {
                                _playerData.Lock();
                            }
                            else if (clientConnection is not null)
                            {
                                _clientConnectionsLock.EnterReadLock();
                            }

                            try
                            {
                                // Send the sized cancellation ACK packet.
                                ReadOnlySpan<byte> cancelSizedAckSpan = [0x00, 0x0C];

                                if ((player is not null && player.Status == PlayerState.TimeWait)
                                    || (clientConnection is not null && clientConnection.Status >= ClientConnectionStatus.Disconnecting))
                                {
                                    // The connection is being disconnected.
                                    // We can only send it unreliably since the SendThread does not process outgoing queues for connections in this state.
                                    SendRaw(conn, cancelSizedAckSpan);
                                }
                                else
                                {
                                    SendOrBufferPacket(conn, cancelSizedAckSpan, NetSendFlags.Reliable);
                                }
                            }
                            finally
                            {
                                if (playerConnection is not null)
                                {
                                    _playerData.Unlock();
                                }
                                else if (clientConnection is not null)
                                {
                                    _clientConnectionsLock.ExitReadLock();
                                }
                            }
                        }
                    }
                }

                return queuedData;
            }

            void SendOutgoingReliable(ConnData conn)
            {
                if (conn is null)
                    return;

                Player? player = null;
                if (conn is PlayerConnection playerConnection)
                {
                    player = playerConnection.Player;
                    if (player is null)
                        return;
                }

                if (player is not null)
                {
                    _playerData.Lock();
                }

                try
                {
                    if (player is null
                        || (player.Status >= PlayerState.Connected && player.Status < PlayerState.TimeWait))
                    {
                        Span<byte> groupedPacketBuffer = stackalloc byte[Constants.MaxGroupedPacketLength];
                        PacketGrouper packetGrouper = new(this, groupedPacketBuffer);

                        if (conn.OutLock.TryEnter())
                        {
                            try
                            {
                                SendOutgoing(conn, ref packetGrouper, BandwidthPriority.Reliable);
                            }
                            finally
                            {
                                conn.OutLock.Exit();
                            }
                        }
                    }
                }
                finally
                {
                    if (player is not null)
                    {
                        _playerData.Unlock();
                    }
                }
            }
        }

        private void SizedSendChunkCompleted(ConnData connData, bool success)
        {
            if (connData is null)
                return;

            Interlocked.Decrement(ref connData.SizedSendQueuedCount);

            if (!success)
                return;

            // Make sure the connection is queued to be processed by the SizedSendThread.
            _sizedSendQueue.TryEnqueue(connData);
        }

        #endregion

        /// <summary>
        /// Processes a data received from a known connection.
        /// </summary>
        /// <remarks>
        /// Core packets are processed synchronously.
        /// Non-core packets (e.g. game or billing packets) are queued to be processed on the mainloop thread.
        /// </remarks>
        /// <param name="conn">Data for the connection that the packet is being processed for.</param>
        /// <param name="data">The buffer to process.</param>
        /// <param name="flags">Flags that describe how the data was received.</param>
        private void ProcessBuffer(ConnData conn, Span<byte> data, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length <= 0)
                return;

            byte t1 = data[0];

            if (t1 == 0x00)
            {
                // First byte of 00 means it's a core packet. Look at the 2nd byte to determine the type of core packet.
                byte t2 = data[1];
                if (t2 < _oohandlers.Length && _oohandlers[t2] is not null)
                {
                    _oohandlers[t2]!(data, conn, flags);
                }
                else
                {
                    if (conn is PlayerConnection playerConnection)
                    {
                        _logManager.LogP(LogLevel.Malicious, nameof(Network), playerConnection.Player!, $"Unknown network subtype {t2}.");
                    }
                    else if (conn is ClientConnection)
                    {
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), $"(client connection) Unknown network subtype {t2}.");
                    }
                }
            }
            else if (t1 < _handlers.Length)
            {
                // It's not a core packet. Process it with a packet handler on the mainloop thread.
                SubspaceBuffer buffer = _bufferPool.Get();
                buffer.Conn = conn;
                buffer.ReceiveFlags = flags;
                data.CopyTo(buffer.Bytes);
                buffer.NumBytes = data.Length;

                Interlocked.Increment(ref conn.ProcessingHolds);
                if (!_mainloop.QueueMainWorkItem(_mainloopWork_CallPacketHandlers, buffer)) // The workitem disposes the buffer.
                {
                    Interlocked.Decrement(ref conn.ProcessingHolds);
                    buffer.Dispose();
                }
            }
            else
            {
                if (conn is PlayerConnection playerConnection)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(Network), playerConnection.Player!, $"Unknown packet type {t1}.");
                }
                else if (conn is ClientConnection)
                {
                    _logManager.LogM(LogLevel.Malicious, nameof(Network), $"(client connection) Unknown packet type {t1}.");
                }
            }
        }

        private void MainloopWork_CallPacketHandlers(SubspaceBuffer buffer)
        {
            if (buffer is null)
                return;

            using (buffer)
            {
                ConnData? conn = buffer.Conn;
                if (conn is null)
                    return;

                try
                {
                    if (buffer.NumBytes < 1)
                        return;

                    CallPacketHandlers(conn, buffer.Bytes.AsSpan(0, buffer.NumBytes), buffer.ReceiveFlags);
                }
                finally
                {
                    Interlocked.Decrement(ref conn.ProcessingHolds);
                }
            }
        }

        private void CallPacketHandlers(ConnData conn, Span<byte> data, NetReceiveFlags flags)
        {
            byte packetType = data[0];

            if (conn is PlayerConnection playerConnection)
            {
                Player? player = playerConnection.Player;
                if (player is null)
                    return;

                // Check if the player is on the way out.
                _playerData.Lock();
                try
                {
                    if (player.Status >= PlayerState.LeavingZone)
                        return;
                }
                finally
                {
                    _playerData.Unlock();
                }

                // Call the handler.
                PacketHandler? handler = null;

                if (packetType < _handlers.Length)
                    handler = _handlers[packetType];

                if (handler is null)
                {
                    _logManager.LogP(LogLevel.Drivel, nameof(Network), player, $"No handler for packet type 0x{packetType:X2}.");
                    return;
                }

                InvokeHandler(handler, player, data, flags);
            }
            else if (conn is ClientConnection clientConnection)
            {
                // Check if the client connection is on the way out.
                _clientConnectionsLock.EnterReadLock();
                try
                {
                    if (clientConnection.Status >= ClientConnectionStatus.Disconnecting)
                        return;
                }
                finally
                {
                    _clientConnectionsLock.ExitReadLock();
                }

                // Call the handler.
                try
                {
                    clientConnection.Handler.HandlePacket(data, flags);
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"(client connection) Handler for packet type 0x{packetType:X2} threw an exception! {ex}.");
                }
            }
            else
            {
                _logManager.LogM(LogLevel.Drivel, nameof(Network), $"Unknown connection type, but got packet type [0x{packetType:X2}] of length {data.Length}.");
            }

            // local helper (for recursion)
            void InvokeHandler(PacketHandler handlers, Player player, Span<byte> data, NetReceiveFlags flags)
            {
                if (handlers.HasSingleTarget)
                {
                    try
                    {
                        handlers(player, data, flags);
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogP(LogLevel.Error, nameof(Network), player, $"Handler for packet type 0x{packetType:X2} threw an exception! {ex}.");
                    }
                }
                else
                {
                    foreach (PacketHandler handler in Delegate.EnumerateInvocationList(handlers))
                    {
                        InvokeHandler(handler, player, data, flags);
                    }
                }
            }
        }

        private void SendToSet(HashSet<Player> set, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            if (set is null)
                return;

            int len = data.Length;

            if (len < 1)
                return;

            /* An important piece of logic that the SendToSet and SendToOne methods take care of (as opposed to the BufferPacket method)
             * is checking whether the data to send is too large to fit into a single packet and if so, split it up.
             * Splitting is done by using 0x00 0x08 and 0x00 0x09 'big' packets.
             * 
             * A limitation is that only one 'big' data transfer can be 'buffering up' at a time for a connection.
             * 'Buffering up' meaning from the point the the first 0x00 0x08 packet is buffered till the closing 0x00 0x09 packet is buffered.
             * In other words, no other 0x00 0x08 or 0x00 0x09 packets can be interleaved with the ones we're sending.
             * 
             * For the SendToOne method, this is managed by holding onto the connection's outgoing lock for the duration the data is
             * being buffered.
             * 
             * For SendToSet, the difference is that it is for a collection of players. Attempting to get a hold on each player's outgoing lock
             * at the same time could likely result in a deadlock.
             * 
             * Here are a few approaches I could think of (each with their pros + and cons -):
             * 1. foreach player, lock the player's outgoing lock, then while splitting the data into 08/09 packets, buffer into the player's outgoing queue
             *    - less efficient: data splitting is repeated once for each player
             * 2. Split the data into 08/09 packets and store them in a collection. foreach player, lock the player's outgoing lock, buffer the 08/09 packets in the collection
             *    + more efficient: data splitting done once
             *    - higher memory footprint: requires a collection and additional memory for each buffer that goes into the collection
             * 3. Hold a 'big data buffering up' lock on the module level. Then, while splitting into 08/09 packets, buffer each packet for each player.
             *    + more efficient: data splitting done once
             *    - less concurrency: the server would only be able to work on one data split at a time
             *    
             * I've opted to go for option #1 due to it's simplicity. Also, sending big data is relatively rare and when it does happen it is being sent to a single player.
             * Currently, the known places where data is too large and gets split include:
             * - 0x0F client settings
             * - 0x03 player entering (when a player enters an arena, that player is sent a jumbo 0x03 containing data for each player in the arena)
             */

            foreach (Player p in set)
            {
                SendToOne(p, data, flags);
            }

            /*
            // OPTION #3 (leaving this here in case there ends up being a case where a set of players gets big data)
            bool isReliable = (flags & NetSendFlags.Reliable) == NetSendFlags.Reliable;

            if ((isReliable && len > Constants.MaxPacket - ReliableHeader.Length)
                || (!isReliable && len > Constants.MaxPacket))
            {
                // use 00 08/09 packets (big data packets)
                // send these reliably (to maintain ordering with sequence #)
                Span<byte> bufferSpan = stackalloc byte[Constants.ChunkSize + 2];
                Span<byte> bufferDataSpan = bufferSpan[2..];
                bufferSpan[0] = 0x00;
                bufferSpan[1] = 0x08;

                int position = 0;

                lock (_playerConnectionsSendBigDataLock)
                {
                    // first send the 08 packets
                    while (len > Constants.ChunkSize)
                    {
                        data.Slice(position, Constants.ChunkSize).CopyTo(bufferDataSpan);
                        SendToSet(set, bufferSpan, flags | NetSendFlags.Reliable); // ASSS only sends the 09 reliably, but I think 08 needs to also be reliable too.  So I added Reliable here.
                        position += Constants.ChunkSize;
                        len -= Constants.ChunkSize;
                    }

                    // final packet is the 09 (signals the end of the big data)
                    bufferSpan[1] = 0x09;
                    data.Slice(position, len).CopyTo(bufferDataSpan);
                    SendToSet(set, bufferSpan.Slice(0, len + 2), flags | NetSendFlags.Reliable);
                }
            }
            else
            {
                foreach (Player p in set)
                {
                    if (p[_connKey] is not ConnData conn)
                        continue;

                    if (!IsOurs(p))
                        continue;

                    lock (conn.olmtx)
                    {
                        BufferPacket(conn, data, flags);
                    }
                }
            }
            */
        }

        private void SendToOne(Player player, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            if (player is null)
                return;

            if (data.Length < 1)
                return;

            if (!IsOurs(player))
                return;

            if (!player.TryGetExtraData(_connKey, out PlayerConnection? conn))
                return;

            SendToOne(conn, data, flags);
        }

        private void SendToOne(ConnData conn, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length < 1)
                return;

            bool isReliable = (flags & NetSendFlags.Reliable) == NetSendFlags.Reliable;

            if ((isReliable && data.Length > Constants.MaxPacket - ReliableHeader.Length)
                || (!isReliable && data.Length > Constants.MaxPacket))
            {
                // The data is too large and has to be fragmented using big data packets (00 08 and 00 09).
                Span<byte> bufferSpan = stackalloc byte[Constants.ChunkSize + 2];
                Span<byte> bufferDataSpan = bufferSpan[2..];
                bufferSpan[0] = 0x00;
                bufferSpan[1] = 0x08;

                // Big data packets are sent reliably (to maintain ordering with sequence #).
                // Only one 'big' data transfer can be 'buffering up' at a time for a connection.
                // 'Buffering up' meaning from the point the the first 0x00 0x08 packet is buffered till the closing 0x00 0x09 packet is buffered.
                // In other words, no other 0x00 0x08 or 0x00 0x09 packets can be interleaved with the ones we're buffering.
                // Therefore, we hold the OutLock the entire time we buffer up the 0x00 0x08 or 0x00 0x09 packets.
                //
                // However, SendOrBufferPacket needs to read the status of the connection, which requires taking the connection status lock.
                // To read the status of player connections, it's the global player data lock.
                // To read the status of client connections, it's the _clientConnectionsLock.
                // The connection status lock must be taken before Outlock, otherwise we risk deadlock.

                PlayerConnection? playerConnection = conn as PlayerConnection;
                ClientConnection? clientConnection = conn as ClientConnection;

                // Lock (connection status).
                if (playerConnection is not null)
                {
                    _playerData.Lock();
                }
                else if (clientConnection is not null)
                {
                    _clientConnectionsLock.EnterReadLock();
                }

                try
                {
                    lock (conn.OutLock)
                    {
                        while (!data.IsEmpty)
                        {
                            ReadOnlySpan<byte> fragment = data[..int.Min(data.Length, Constants.ChunkSize)];
                            fragment.CopyTo(bufferDataSpan);

                            data = data[fragment.Length..];
                            if (data.IsEmpty)
                            {
                                // The final packet is 00 09 (signals the end of the big data)
                                bufferSpan[1] = 0x09;
                            }

                            SendOrBufferPacket(conn, bufferSpan[..(2 + fragment.Length)], flags | NetSendFlags.Reliable);
                        }
                    }
                }
                finally
                {
                    // Unlock (connection status).
                    if (playerConnection is not null)
                    {
                        _playerData.Unlock();
                    }
                    else if (clientConnection is not null)
                    {
                        _clientConnectionsLock.ExitReadLock();
                    }
                }
            }
            else
            {
                SendOrBufferPacket(conn, data, flags);
            }
        }

        private bool SendWithCallback(Player player, ReadOnlySpan<byte> data, IReliableCallbackInvoker callbackInvoker)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentOutOfRangeException.ThrowIfLessThan(data.Length, 1, nameof(data));
            ArgumentNullException.ThrowIfNull(callbackInvoker);

            if (!IsOurs(player))
                return false;

            if (!player.TryGetExtraData(_connKey, out PlayerConnection? conn))
                return false;

            // we can't handle big packets here
            Debug.Assert(data.Length <= (Constants.MaxPacket - ReliableHeader.Length));

            return SendOrBufferPacket(conn, data, NetSendFlags.Reliable, callbackInvoker);
        }

        private bool SendWithCallback(ClientConnection clientConnection, ReadOnlySpan<byte> data, IReliableCallbackInvoker callbackInvoker)
        {
            ArgumentNullException.ThrowIfNull(clientConnection);
            ArgumentOutOfRangeException.ThrowIfLessThan(data.Length, 1, nameof(data));
            ArgumentNullException.ThrowIfNull(callbackInvoker);

            // we can't handle big packets here
            Debug.Assert(data.Length <= (Constants.MaxPacket - ReliableHeader.Length));

            return SendOrBufferPacket(clientConnection, data, NetSendFlags.Reliable, callbackInvoker);
        }

        /// <summary>
        /// Immediately sends data to a known connection.
        /// </summary>
        /// <remarks>
        /// Bandwidth limits are ignored by this method.
        /// </remarks>
        /// <param name="conn">Context about the connection.</param>
        /// <param name="data">The data to send.</param>
        private void SendRaw(ConnData conn, ReadOnlySpan<byte> data)
        {
            if (conn is null)
                return;

            int length = data.Length;

            PlayerConnection? playerConnection = conn as PlayerConnection;
            ClientConnection? clientConnection = conn as ClientConnection;
            Player? player = null;
            if (playerConnection is not null)
            {
                player = playerConnection.Player;
                if (player is null)
                    return;
            }

            try
            {
                if (player is not null)
                {
                    _playerData.Lock();

                    if (player.Status == PlayerState.Uninitialized)
                        return;
                }
                else if (clientConnection is not null)
                {
                    _clientConnectionsLock.EnterReadLock();

                    if (clientConnection.Status == ClientConnectionStatus.Disconnected)
                        return;
                }

#if CFG_DUMP_RAW_PACKETS
                if (player is not null)
                    DumpPk($"SEND: {length} bytes to {player.Id}", data);
                else if (clientConnection is not null)
                    DumpPk($"SEND: {length} bytes to client connection {conn.RemoteEndpoint}", data);
#endif

                Span<byte> encryptedBuffer = stackalloc byte[Constants.MaxPacket + 4];
                data.CopyTo(encryptedBuffer);

                if (playerConnection is not null && playerConnection.Encryptor is not null)
                {
                    length = playerConnection.Encryptor.Encrypt(player!, encryptedBuffer, length);
                }
                else if (clientConnection is not null && clientConnection.Encryptor is not null)
                {
                    length = clientConnection.Encryptor.Encrypt(clientConnection, encryptedBuffer, length);
                }

                if (length == 0)
                    return;

                encryptedBuffer = encryptedBuffer[..length];

#if CFG_DUMP_RAW_PACKETS
                if (player is not null)
                    DumpPk($"SEND: {length} bytes to pid {player.Id} (after encryption)", encryptedBuffer);
                else if (clientConnection is not null)
                    DumpPk($"SEND: {length} bytes to client connection {conn.RemoteEndpoint} (after encryption)", encryptedBuffer);
#endif

                try
                {
                    conn.SendSocket!.SendTo(encryptedBuffer, SocketFlags.None, conn.RemoteAddress!);
                }
                catch (SocketException ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"SocketException with error code {ex.ErrorCode} when sending to {conn.RemoteEndpoint} with socket {conn.SendSocket!.LocalEndPoint}. {ex}");
                    return;
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"Exception when sending to {conn.RemoteEndpoint} with socket {conn.SendSocket!.LocalEndPoint}. {ex}");
                    return;
                }
            }
            finally
            {
                if (player is not null)
                {
                    _playerData.Unlock();
                }
                else if (clientConnection is not null)
                {
                    _clientConnectionsLock.ExitReadLock();
                }
            }

            Interlocked.Exchange(ref conn.LastSendTimestamp, Stopwatch.GetTimestamp());
            Interlocked.Add(ref conn.BytesSent, (ulong)length);
            Interlocked.Increment(ref conn.PacketsSent);
            Interlocked.Add(ref _globalStats.BytesSent, (ulong)length);
            Interlocked.Increment(ref _globalStats.PacketsSent);
        }

        /// <summary>
        /// Sends or buffers data to be sent to a known connection.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This will attempt to send <see cref="NetSendFlags.Urgent"/> packets immediately (given there is enough bandwidth available).
        /// If there isn't enough bandwidth available and the urgent packet is <see cref="NetSendFlags.Droppable"/>, it will be dropped.
        /// Otherwise, the urgent packet will be buffered to be sent.
        /// </para>
        /// <para>
        /// Packets that are not <see cref="NetSendFlags.Urgent"/> are buffered to be sent.
        /// </para>
        /// </remarks>
        /// <param name="conn">Context about the connection.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags describing how the data should be sent.</param>
        /// <param name="callbackInvoker">An optional callback for reliable packets.</param>
        /// <returns><see langword="true"/> if the data was sent or buffered. <see langword="false"/> if the data was dropped.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="conn"/> was null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="data"/> was empty.</exception>
        private bool SendOrBufferPacket(ConnData conn, ReadOnlySpan<byte> data, NetSendFlags flags, IReliableCallbackInvoker? callbackInvoker = null)
        {
            ArgumentNullException.ThrowIfNull(conn);

            int len = data.Length;
            if (len < 1)
                throw new ArgumentOutOfRangeException(nameof(data), "Length must be at least 1.");

            PlayerConnection? playerConnection = conn as PlayerConnection;
            ClientConnection? clientConnection = conn as ClientConnection;

            bool isReliable = (flags & NetSendFlags.Reliable) == NetSendFlags.Reliable;

            //
            // Check some conditions that should be true and would normally be caught when developing in debug mode.
            //

            // TODO: If OutLock is already being held, then the lock to read connection status must already be held too.
            //Debug.Assert(
            //    !Monitor.IsEntered(conn.OutLock)
            //    || (Monitor.IsEntered(conn.OutLock)
            //        && ((playerConnection is not null && (_playerData.IsReadLockHeld || _playerData.IsWriteLockHeld || _playerData.IsUpgradeableReadLockHeld))
            //            || (clientConnection is not null && (_clientConnectionsLock.IsReadLockHeld || _clientConnectionsLock.IsWriteLockHeld || _clientConnectionsLock.IsUpgradeableReadLockHeld))
            //        )
            //    )
            //);

            // data has to be able to fit (a reliable packet has an additional header, so account for that too)
            Debug.Assert((isReliable && len <= Constants.MaxPacket - ReliableHeader.Length) || (!isReliable && len <= Constants.MaxPacket));

            // you can't buffer already-reliable packets
            Debug.Assert(!(len >= 2 && data[0] == 0x00 && data[1] == 0x03));

            // reliable packets can't be droppable
            Debug.Assert((flags & (NetSendFlags.Reliable | NetSendFlags.Droppable)) != (NetSendFlags.Reliable | NetSendFlags.Droppable));

            // If there's a callback, then it must be reliable.
            Debug.Assert(callbackInvoker is null || (flags & NetSendFlags.Reliable) == NetSendFlags.Reliable);

            try
            {
                if (playerConnection is not null)
                {
                    _playerData.Lock();

                    Player? player = playerConnection.Player;
                    if (player is null)
                        return false;

                    // Don't send or buffer data if the player is being disconnected.
                    if (player.Status >= PlayerState.TimeWait)
                        return false;
                }
                else if (clientConnection is not null)
                {
                    _clientConnectionsLock.EnterReadLock();

                    // Send or buffer data only if connected.
                    if (clientConnection.Status != ClientConnectionStatus.Connected)
                        return false;
                }

                //
                // Determine the priority.
                //

                BandwidthPriority pri;

                if ((flags & NetSendFlags.Ack) == NetSendFlags.Ack)
                {
                    pri = BandwidthPriority.Ack;
                }
                else if (isReliable)
                {
                    pri = BandwidthPriority.Reliable;
                }
                else
                {
                    // figure out priority (ignoring the reliable, droppable, and urgent flags)
                    pri = ((int)flags & 0x70) switch
                    {
                        (int)NetSendFlags.PriorityN1 & 0x70 => BandwidthPriority.UnreliableLow,
                        (int)NetSendFlags.PriorityP4 & 0x70 or (int)NetSendFlags.PriorityP5 & 0x70 => BandwidthPriority.UnreliableHigh,
                        _ => BandwidthPriority.Unreliable,
                    };
                }

                // update global stats based on requested priority
                Interlocked.Add(ref _globalStats.PriorityStats[(int)pri], (ulong)len);

                lock (conn.OutLock)
                {
                    //
                    // Check if it can be sent immediately instead of being buffered.
                    //

                    // try the fast path
                    if ((flags & (NetSendFlags.Urgent | NetSendFlags.Reliable)) == NetSendFlags.Urgent)
                    {
                        // urgent and not reliable
                        if (conn.BandwidthLimiter!.Check(len + _config.PerPacketOverhead, pri, true))
                        {
                            SendRaw(conn, data);
                            return true;
                        }
                        else
                        {
                            if ((flags & NetSendFlags.Droppable) == NetSendFlags.Droppable)
                            {
                                Interlocked.Increment(ref conn.PacketsDropped);
                                return false;
                            }
                        }
                    }

                    //
                    // Buffer the packet.
                    //

                    SubspaceBuffer buf = _bufferPool.Get();
                    buf.Conn = conn;
                    buf.LastTryTimestamp = null;
                    buf.Tries = 0;
                    buf.CallbackInvoker = callbackInvoker;
                    buf.SendFlags = flags;
                    buf.NumBytes = len;
                    data.CopyTo(buf.Bytes);

                    LinkedListNode<SubspaceBuffer> node = _bufferNodePool.Get();
                    node.Value = buf;

                    if ((flags & NetSendFlags.Reliable) == NetSendFlags.Reliable)
                    {
                        conn.UnsentRelOutList.AddLast(node);
                    }
                    else
                    {
                        conn.OutList[(int)pri].AddLast(node);
                    }

                    return true;
                }
            }
            finally
            {
                if (playerConnection is not null)
                {
                    _playerData.Unlock();
                }
                else if (clientConnection is not null)
                {
                    _clientConnectionsLock.ExitReadLock();
                }
            }
        }

        private void ExecuteReliableCallback(IReliableCallbackInvoker? callbackInvoker, ConnData conn, bool success)
        {
            ArgumentNullException.ThrowIfNull(callbackInvoker);
            ArgumentNullException.ThrowIfNull(conn);

            do
            {
                IReliableCallbackInvoker? next = callbackInvoker.Next;

                InvokeReliableCallbackWork work = new()
                {
                    CallbackInvoker = callbackInvoker,
                    ConnData = conn,
                    Success = success,
                };

                switch (callbackInvoker.ExecutionOption)
                {
                    case ReliableCallbackExecutionOption.Synchronous:
                        InvokeReliableCallback(work);
                        break;

                    case ReliableCallbackExecutionOption.ThreadPool:
                        Interlocked.Increment(ref conn.ProcessingHolds);
                        if (!_mainloop.QueueThreadPoolWorkItem(AsyncInvokeReliableCallback, work))
                        {
                            Interlocked.Decrement(ref conn.ProcessingHolds);
                        }
                        break;

                    case ReliableCallbackExecutionOption.Mainloop:
                    default:
                        Interlocked.Increment(ref conn.ProcessingHolds);
                        if (!_mainloop.QueueMainWorkItem(AsyncInvokeReliableCallback, work))
                        {
                            Interlocked.Decrement(ref conn.ProcessingHolds);
                        }
                        break;
                }

                callbackInvoker = next;
            }
            while (callbackInvoker is not null);


            static void InvokeReliableCallback(InvokeReliableCallbackWork work)
            {
                using (work.CallbackInvoker)
                {
                    ConnData conn = work.ConnData;
                    if (conn is null)
                        return;

                    work.CallbackInvoker?.Invoke(conn, work.Success);
                }
            }

            static void AsyncInvokeReliableCallback(InvokeReliableCallbackWork work)
            {
                using (work.CallbackInvoker)
                {
                    ConnData conn = work.ConnData;
                    if (conn is null)
                        return;

                    try
                    {
                        work.CallbackInvoker?.Invoke(conn, work.Success);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref conn.ProcessingHolds);
                    }
                }
            }
        }

        /// <summary>
        /// Logic to run at the end of sized receive.
        /// </summary>
        /// <remarks>Call with <see cref="ConnData.BigLock"/> locked.</remarks>
        /// <param name="player"></param>
        /// <param name="success"></param>
        private void EndSizedReceive(Player player, bool success)
        {
            if (player is null)
                return;

            if (!player.TryGetExtraData(_connKey, out PlayerConnection? conn))
                return;

            if (conn.SizedRecv.Offset != 0)
            {
                int type = conn.SizedRecv.Type;
                int arg = success ? conn.SizedRecv.TotalLength : -1;

                // Tell the handlers that the transfer completed (successfully or cancelled).
                if (type < _sizedhandlers.Length)
                {
                    _sizedhandlers[type]?.Invoke(player, [], arg, arg);
                }

                conn.SizedRecv.Type = 0;
                conn.SizedRecv.TotalLength = 0;
                conn.SizedRecv.Offset = 0;
            }
        }

        private void SetDisconnecting(ClientConnection clientConnection)
        {
            ArgumentNullException.ThrowIfNull(clientConnection);

            _clientConnectionsLock.EnterWriteLock();
            try
            {
                if (clientConnection.Status < ClientConnectionStatus.Disconnecting)
                {
                    clientConnection.Status = ClientConnectionStatus.Disconnecting;
                }
            }
            finally
            {
                _clientConnectionsLock.ExitWriteLock();
            }
        }

        private void MainloopWork_ClientConnectionHandlerDisconnected(ClientConnection clientConnection)
        {
            if (clientConnection is null)
                return;

            clientConnection.Handler.Disconnected();
            _clientConnectionPool.Return(clientConnection);
        }

        private static bool IsOurs(Player player)
        {
            return player.Type == ClientType.Continuum || player.Type == ClientType.VIE;
        }

        private static void GetConnectionStats(ConnData conn, ref NetConnectionStats stats)
        {
            if (conn is null)
                return;

            stats.PacketsSent = Interlocked.CompareExchange(ref conn.PacketsSent, 0, 0);
            stats.PacketsReceived = Interlocked.CompareExchange(ref conn.PacketsReceived, 0, 0);
            stats.ReliablePacketsSent = Interlocked.Read(ref conn.ReliablePacketsSent);
            stats.ReliablePacketsReceived = Interlocked.Read(ref conn.ReliablePacketsReceived);
            stats.BytesSent = Interlocked.Read(ref conn.BytesSent);
            stats.BytesReceived = Interlocked.Read(ref conn.BytesReceived);
            stats.RelDups = Interlocked.Read(ref conn.RelDups);
            stats.AckDups = Interlocked.Read(ref conn.AckDups);
            stats.Retries = Interlocked.Read(ref conn.Retries);
            stats.PacketsDropped = Interlocked.Read(ref conn.PacketsDropped);

            stats.EncryptorName = conn.EncryptorName;
            stats.IPEndPoint = conn.RemoteEndpoint!;

            if (stats.BandwidthLimitInfo is not null)
            {
                lock (conn.OutLock)
                {
                    conn.BandwidthLimiter?.GetInfo(stats.BandwidthLimitInfo);
                }
            }
        }

        [Conditional("CFG_DUMP_RAW_PACKETS")]
        private void DumpPk(string description, ReadOnlySpan<byte> d)
        {
            StringBuilder sb = _objectPoolManager.StringBuilderPool.Get();

            try
            {
                sb.AppendLine(description);

                int pos = 0;
                StringBuilder asciiBuilder = _objectPoolManager.StringBuilderPool.Get();

                try
                {
                    while (pos < d.Length)
                    {
                        int c;

                        for (c = 0; c < 16 && pos < d.Length; c++, pos++)
                        {
                            if (c > 0)
                                sb.Append(' ');

                            asciiBuilder.Append(!char.IsControl((char)d[pos]) ? (char)d[pos] : '.');
                            sb.Append($"{d[pos]:X2}");
                        }

                        for (; c < 16; c++)
                        {
                            sb.Append("   ");
                        }

                        sb.Append("  ");
                        sb.Append(asciiBuilder);
                        sb.AppendLine();
                        asciiBuilder.Length = 0;
                    }
                }
                finally
                {
                    _objectPoolManager.StringBuilderPool.Return(asciiBuilder);
                }

                Debug.Write(sb.ToString());
            }
            finally
            {
                _objectPoolManager.StringBuilderPool.Return(sb);
            }
        }

        #region Helper types

        /// <summary>
        /// Base class for a connection.
        /// </summary>
        private abstract class ConnData : IResettable, IDisposable
        {
            /// <summary>
            /// The remote address to communicate with.
            /// </summary>
            public IPEndPoint? RemoteEndpoint;

            /// <summary>
            /// The remote address to communicate with.
            /// </summary>
            public SocketAddress? RemoteAddress;

            /// <summary>
            /// Which socket to use when sending.
            /// </summary>
            /// <remarks>
            /// For <see cref="PlayerConnection"/>s, this is a game socket.
            /// For <see cref="ClientConnection"/>s, this is the client socket.
            /// </remarks>
            public Socket? SendSocket;

            /// <summary>
            /// The name of the encryptor interface.
            /// </summary>
            public string? EncryptorName;

            /// <summary>
            /// Timestamp that data was last sent.
            /// </summary>
            /// <remarks>
            /// Synchronized using <see cref="Interlocked"/> methods.
            /// </remarks>
            public long LastSendTimestamp;

            /// <summary>
            /// Timestamp that data was last received.
            /// </summary>
            /// <remarks>
            /// Synchronized using <see cref="Interlocked"/> methods.
            /// </remarks>
            public long LastReceiveTimestamp;

            /// <summary>
            /// The number of packets sent.
            /// </summary>
            /// <remarks>
            /// Synchronized using <see cref="Interlocked"/> methods.
            /// </remarks>
            public uint PacketsSent;

            /// <summary>
            /// The number of packets received.
            /// </summary>
            /// <remarks>
            /// Synchronized using <see cref="Interlocked"/> methods.
            /// </remarks>
            public uint PacketsReceived;

            /// <summary>
            /// The number of reliable packets sent, or in the process of being sent.
            /// </summary>
            /// <remarks>
            /// More specifically, this value is the # of packets that have been assigned a reliable sequence number, having been placed in the <see cref="OutList"/>.
            /// Retries do not affect this count. No matter how many times a packet is retried, it only counts once.
            /// <para>
            /// The value is equal to <see cref="SeqNumOut"/>, until <see cref="SeqNumOut"/> overflows.
            /// </para>
            /// <para>
            /// Synchronized using <see cref="Interlocked"/> methods.
            /// </para>
            /// </remarks>
            public ulong ReliablePacketsSent;

            /// <summary>
            /// The number of reliable packets that have been received.
            /// </summary>
            /// <remarks>
            /// More specifically, this value is the # of reliable packet received, and placed into the incoming reliable queue to be processed.
            /// Receiving duplicate reliable packets does not affect this count. No matter how many duplicates received, it only counts once.
            /// <para>
            /// This is *roughly* equivalent to <see cref="ReliableBuffer"/>.CurrentSequenceNum.
            /// This count is about reliable packets received, and not necessarily processed yet.
            /// Whereas <see cref="ReliableBuffer"/>.CurrentSequenceNum is about reliable packets processed.
            /// Processing happens later, especially so if received out of order (e.g. packetloss or took a different route) since reliable packets can only be processed in the order of their sequence.
            /// </para>
            /// <para>
            /// Synchronized using <see cref="Interlocked"/> methods.
            /// </para>
            /// </remarks>
            public ulong ReliablePacketsReceived;

            /// <summary>
            /// The number  of bytes sent.
            /// </summary>
            /// <remarks>
            /// Synchronized using <see cref="Interlocked"/> methods.
            /// </remarks>
            public ulong BytesSent;

            /// <summary>
            /// The number of bytes received.
            /// </summary>
            /// <remarks>
            /// Synchronized using <see cref="Interlocked"/> methods.
            /// </remarks>
            public ulong BytesReceived;

            /// <summary>
            /// The number of duplicate reliable packets received.
            /// </summary>
            /// <remarks>
            /// Synchronized using <see cref="Interlocked"/> methods.
            /// </remarks>
            public ulong RelDups;

            /// <summary>
            /// The number of ACKs received that did not correspond to a sent, waiting to be acknowledged, reliable packet.
            /// </summary>
            /// <remarks>
            /// Synchronized using <see cref="Interlocked"/> methods.
            /// </remarks>
            public ulong AckDups;

            /// <summary>
            /// The number of times outgoing reliable data was resent.
            /// </summary>
            /// <remarks>
            /// Synchronized using <see cref="Interlocked"/> methods.
            /// </remarks>
            public ulong Retries;

            /// <summary>
            /// The number of outgoing packets dropped due to bandwidth limits.
            /// </summary>
            /// <remarks>
            /// Synchronized using <see cref="Interlocked"/> methods.
            /// </remarks>
            public ulong PacketsDropped;

            public class SizedReceive
            {
                public int Type;
                public int TotalLength, Offset;
            }

            /// <summary>
            /// Helper for receiving sized data packets.
            /// </summary>
            /// <remarks>
            /// Synchronized with <see cref="BigLock"/>.
            /// </remarks>
            public readonly SizedReceive SizedRecv = new();

            /// <summary>
            /// Helper for receiving big data packets.
            /// </summary>
            /// <remarks>
            /// Synchronized with <see cref="BigLock"/>.
            /// </remarks>
            public BigReceive? BigReceive;

            /// <summary>
            /// The # of sized send packets that have been queued and not yet ACK'd.
            /// </summary>
            /// <remarks>
            /// Synchronized with <see cref="Interlocked"/>.
            /// </remarks>
            public int SizedSendQueuedCount;

            /// <summary>
            /// For synchronizing <see cref="SizedSends"/>.
            /// </summary>
            public readonly Lock SizedSendLock = new();

            /// <summary>
            /// Queue of pending sized sends for the connection.
            /// </summary>
            /// <remarks>
            /// Synchronized with <see cref="SizedSendLock"/>.
            /// </remarks>
            public readonly LinkedList<ISizedSendData> SizedSends = new();

            /// <summary>
            /// The sequence number for outgoing reliable packets.
            /// </summary>
            /// Synchronized using <see cref="OutLock"/>, though only used by the <see cref="SendThread"/>.
            public int SeqNumOut;

            /// <summary>
            /// Whether a reliable packet was retried the maximum # of times.
            /// When set to true, it means the connection should be dropped.
            /// </summary>
            /// <remarks>
            /// Synchronized using <see cref="OutLock"/>.
            /// </remarks>
            public bool HitMaxRetries;

            /// <summary>
            /// Whether there are too many packets pending in a connection's outgoing queues.
            /// Packets that are considered as being pending include:
            /// <list type="bullet">
            /// <item>packets that have not yet been sent (due to bandwidth limits)</item>
            /// <item>reliable packets that have been sent but have not yet been acknowledged</item>
            /// </list>
            /// When set to true, it means the connection should be dropped.
            /// </summary>
            /// <remarks>
            /// Synchronized using <see cref="OutLock"/>.
            /// </remarks>
            public bool HitMaxOutlist;

            /// <summary>
            /// The average roundtrip time.
            /// </summary>
            /// <remarks>
            /// This value is based on how long it takes to get an ACK back after sending a reliable packet.
            /// It is used to determine when to resend a reliable packet that has not yet been ACK'd.
            /// <para>
            /// Synchronized with <see cref="OutLock"/>.
            /// </para>
            /// </remarks>
            public int AverageRoundTripTime;

            /// <summary>
            /// The average deviation in the <see cref="AverageRoundTripTime"/>.
            /// </summary>
            /// <remarks>
            /// Synchronized with <see cref="OutLock"/>.
            /// </remarks>
            public int AverageRoundTripDeviation;

            /// <summary>
            /// The bandwidth limiter provider to use for the connection. <see langword="null"/> if using the default (<see cref="_bandwidthLimiterProvider"/>).
            /// </summary>
            public IBandwidthLimiterProvider? BandwidthLimiterProvider;

            /// <summary>
            /// The name of the <see cref="IBandwidthLimiterProvider"/> interface. <see langword="null"/> if using the default (<see cref="_bandwidthLimiterProvider"/>).
            /// </summary>
            public string? BandwidthLimiterProviderName;

            /// <summary>
            /// The bandwidth limiter to use for the connection.
            /// </summary>
            /// <remarks>
            /// Synchronized with <see cref="OutLock"/>.
            /// </remarks>
            public IBandwidthLimiter? BandwidthLimiter;

            /// <summary>
            /// Array of outgoing lists that acts like a type of priority queue.  Indexed by <see cref="BandwidthPriority"/>.
            /// </summary>
            /// <remarks>
            /// Synchronized with <see cref="OutLock"/>.
            /// </remarks>
            public readonly LinkedList<SubspaceBuffer>[] OutList;

            /// <summary>
            /// Unsent outgoing reliable packet queue.
            /// Packets in this queue do not have a sequence number assigned yet.
            /// </summary>
            /// <remarks>
            /// Note: Reliable packets that are in the process of being sent are in <see cref="OutList"/>[<see cref="BandwidthPriority.Reliable"/>].
            /// <para>
            /// Synchronized with <see cref="OutLock"/>.
            /// </para>
            /// </remarks>
            public readonly LinkedList<SubspaceBuffer> UnsentRelOutList = new();

            /// <summary>
            /// Buffer for incoming reliable packets.
            /// </summary>
            /// <remarks>
            /// Synchronized with <see cref="ReliableLock"/>.
            /// </remarks>
            public readonly CircularSequenceQueue<SubspaceBuffer> ReliableBuffer;

            /// <summary>
            /// Lock object for synchronizing processing of outgoing data.
            /// </summary>
            public readonly Lock OutLock = new();

            /// <summary>
            /// Lock object for synchronizing processing of incoming reliable data.
            /// </summary>
            public readonly Lock ReliableLock = new();

            /// <summary>
            /// This is used to ensure that only one incoming reliable packet is processed at a given time for the connection.
            /// </summary>
            /// <remarks>
            /// Reliable packets need to be processed in the order of their sequence number; two can't be processed simultaneously.
            /// <see cref="ReliableLock"/> is not held while processing since the receive thread shouldn't be blocked from adding to <see cref="ReliableBuffer"/>.
            /// So, this is needed in case there are multiple threads processing reliable packets (Net:ReliableThreads > 1).
            /// </remarks>
            public readonly Lock ReliableProcessingLock = new();

            /// <summary>
            /// Lock object for synchronizing processing of incoming big data and incoming sized data
            /// </summary>
            public readonly Lock BigLock = new();

            /// <summary>
            /// A count of ongoing processing occurring, including any asynchronous work queued.
            /// </summary>
            /// <remarks>
            /// This is synchronized using <see cref="Interlocked"/> in combination with locking to prevent state changes
            /// (of <see cref="Player.Status"/> or <see cref="ClientConnection.Status"/>) during critical increments of this value (from 0 to 1).
            /// When this value is incremented from 0 to 1, the connection is guaranteed to not be in a status that meets the disconnect criteria.
            /// To ensure the status doesn't change while the value is being incremented, a lock is held during the increment.
            /// For players, the global player data lock is held.
            /// For client connections, the <see cref="_clientConnectionsLock"/> is held.
            /// This way, it is impossible for the count to change from 0 to 1 after the status is changed to meet the disconnect criteria.
            /// In places where this value is guaranteed to already be > 0, the value is simply incremented without the need for holding a lock.
            /// </remarks>
            public int ProcessingHolds;

            /// <summary>
            /// Initializes a new instance of the <see cref="ConnData"/> class with a specified length for the incoming reliable data buffer.
            /// </summary>
            /// <param name="reliableBufferLength">The length of the incoming reliable data buffer.</param>
            protected ConnData(int reliableBufferLength)
            {
                OutList = new LinkedList<SubspaceBuffer>[(int)Enum.GetValues<BandwidthPriority>().Max() + 1];

                for (int x = 0; x < OutList.Length; x++)
                {
                    OutList[x] = new LinkedList<SubspaceBuffer>();
                }

                ReliableBuffer = new(reliableBufferLength);
            }

            protected void Initialize()
            {
                long timestamp = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref LastSendTimestamp, timestamp);
                Interlocked.Exchange(ref LastReceiveTimestamp, timestamp);

                Interlocked.Exchange(ref SizedSendQueuedCount, 0);

                lock (SizedSendLock)
                {
                    SizedSends.Clear();
                }

                lock (OutLock)
                {
                    AverageRoundTripTime = 200; // an initial guess
                    AverageRoundTripDeviation = 100;

                    for (int x = 0; x < OutList.Length; x++)
                    {
                        OutList[x].Clear();
                    }

                    UnsentRelOutList.Clear();
                }
            }

            public virtual bool TryReset()
            {
                RemoteAddress = null;
                RemoteEndpoint = null;
                SendSocket = null;

                Interlocked.Exchange(ref LastSendTimestamp, 0);
                Interlocked.Exchange(ref LastReceiveTimestamp, 0);
                Interlocked.Exchange(ref PacketsSent, 0);
                Interlocked.Exchange(ref PacketsReceived, 0);
                Interlocked.Exchange(ref ReliablePacketsSent, 0);
                Interlocked.Exchange(ref ReliablePacketsReceived, 0);
                Interlocked.Exchange(ref BytesSent, 0);
                Interlocked.Exchange(ref BytesReceived, 0);
                Interlocked.Exchange(ref RelDups, 0);
                Interlocked.Exchange(ref AckDups, 0);
                Interlocked.Exchange(ref Retries, 0);
                Interlocked.Exchange(ref PacketsDropped, 0);

                EncryptorName = null;

                lock (BigLock)
                {
                    SizedRecv.Type = 0;
                    SizedRecv.TotalLength = 0;
                    SizedRecv.Offset = 0;

                    if (BigReceive is not null)
                    {
                        BigReceive.Dispose();
                        BigReceive = null;
                    }
                }

                BandwidthLimiter = null;

                Interlocked.Exchange(ref SizedSendQueuedCount, 0);

                lock (SizedSendLock)
                {
                    SizedSends.Clear();
                }

                lock (OutLock)
                {
                    SeqNumOut = 0;

                    HitMaxRetries = false;
                    HitMaxOutlist = false;

                    AverageRoundTripTime = 0;
                    AverageRoundTripDeviation = 0;

                    for (int i = 0; i < OutList.Length; i++)
                    {
                        foreach (SubspaceBuffer buffer in OutList[i])
                            buffer.Dispose();

                        OutList[i].Clear();
                    }

                    foreach (SubspaceBuffer buffer in UnsentRelOutList)
                        buffer.Dispose();

                    UnsentRelOutList.Clear();
                }

                lock (ReliableLock)
                {
                    ReliableBuffer.Reset();
                }

                return true;
            }

            protected virtual void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    TryReset();
                }
            }

            public void Dispose()
            {
                Dispose(true);
            }
        }

        /// <summary>
        /// Represents a connection for a <see cref="Player"/> that is connected to us, the server.
        /// </summary>
        /// <param name="reliableBufferLength"></param>
        private sealed class PlayerConnection(int reliableBufferLength) : ConnData(reliableBufferLength)
        {
            /// <summary>
            /// The player this connection is for.
            /// </summary>
            public Player? Player;

            /// <summary>
            /// For encrypting and decrypting data for the connection.
            /// </summary>
            public IEncrypt? Encryptor;

            public void Initialize(IEncrypt? encryptor, string? encryptorName, IBandwidthLimiter bandwidthLimiter)
            {
                Initialize();

                Encryptor = encryptor;
                EncryptorName = encryptorName;
                BandwidthLimiter = bandwidthLimiter ?? throw new ArgumentNullException(nameof(bandwidthLimiter));
            }

            public override bool TryReset()
            {
                Player = null;
                Encryptor = null;

                return base.TryReset();
            }
        }

        private class PlayerConnectionPooledObjectPolicy(int reliableBufferLength) : IPooledObjectPolicy<PlayerConnection>
        {
            private readonly int _reliableBufferLength = reliableBufferLength;

            public PlayerConnection Create()
            {
                return new PlayerConnection(_reliableBufferLength);
            }

            public bool Return(PlayerConnection obj)
            {
                if (obj is null)
                    return false;

                return obj.TryReset();
            }
        }

        /// <summary>
        /// Represents a connection to a server where this side is acting as the client. For example, a connection to a billing server.
        /// </summary>
        private sealed class ClientConnection(int reliableBufferLength) : ConnData(reliableBufferLength), IClientConnection
        {
            public void Initialize(
                IPEndPoint remoteEndpoint,
                Socket socket,
                IClientConnectionHandler handler,
                IClientEncrypt encryptor,
                string encryptorName,
                IBandwidthLimiterProvider bandwidthLimiterProvider,
                string bandwidthLimiterProviderName)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(encryptorName);
                ArgumentException.ThrowIfNullOrWhiteSpace(bandwidthLimiterProviderName);

                Initialize();

                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
                RemoteEndpoint = remoteEndpoint ?? throw new ArgumentNullException(nameof(remoteEndpoint));
                RemoteAddress = remoteEndpoint.Serialize();
                SendSocket = socket ?? throw new ArgumentNullException(nameof(socket));
                Encryptor = encryptor ?? throw new ArgumentNullException(nameof(encryptor));
                EncryptorName = encryptorName;
                BandwidthLimiterProvider = bandwidthLimiterProvider ?? throw new ArgumentNullException(nameof(bandwidthLimiterProvider));
                BandwidthLimiterProviderName = bandwidthLimiterProviderName;
                BandwidthLimiter = bandwidthLimiterProvider.New();
                Status = ClientConnectionStatus.Connecting;

                Encryptor.Initialize(this);
            }

            private IClientConnectionHandler? _handler;

            /// <summary>
            /// The handler for the client connection.
            /// </summary>
            public IClientConnectionHandler Handler
            {
                get
                {
                    if (_handler is null)
                        throw new InvalidOperationException();

                    return _handler;
                }
            }

            /// <summary>
            /// For encrypting and decrypting data for the connection.
            /// </summary>
            public IClientEncrypt? Encryptor;

            /// <summary>
            /// The status of the client connection.
            /// </summary>
            /// <remarks>Synchronized using <see cref="_clientConnectionsLock"/>.</remarks>
            public ClientConnectionStatus Status;

            #region Extra Data

            private readonly ConcurrentDictionary<Type, object> _extraData = new();

            public bool TryAddExtraData<T>(T value) where T : class
            {
                return _extraData.TryAdd(typeof(T), value);
            }

            public bool TryGetExtraData<T>([MaybeNullWhen(false)] out T value) where T : class
            {
                if (_extraData.TryGetValue(typeof(T), out object? obj))
                {
                    value = (T)obj;
                    return true;
                }
                else
                {
                    value = default;
                    return false;
                }
            }

            public bool TryRemoveExtraData<T>([MaybeNullWhen(false)] out T value) where T : class
            {
                if (_extraData.TryRemove(typeof(T), out object? obj))
                {
                    value = (T)obj;
                    return true;
                }
                else
                {
                    value = default;
                    return false;
                }
            }

            #endregion

            public override bool TryReset()
            {
                _handler = null;
                Encryptor = null;
                Status = ClientConnectionStatus.Disconnected;

                return base.TryReset();
            }
        }

        public enum ClientConnectionStatus
        {
            Connecting,
            Connected,
            Disconnecting,
            Disconnected,
        }

        private class ClientConnectionPooledObjectPolicy(int reliableBufferLength) : IPooledObjectPolicy<ClientConnection>
        {
            private readonly int _reliableBufferLength = reliableBufferLength;

            public ClientConnection Create()
            {
                return new ClientConnection(_reliableBufferLength);
            }

            public bool Return(ClientConnection obj)
            {
                if (obj is null)
                    return false;

                return obj.TryReset();
            }
        }

        /// <summary>
        /// A specialized data buffer which keeps track of what connection it is for and other useful info.
        /// </summary>
        private sealed class SubspaceBuffer : PooledObject
        {
            /// <summary>
            /// The actual buffer for storing data.
            /// </summary>
            public readonly byte[] Bytes = new byte[Constants.MaxPacket];

            /// <summary>
            /// The number of bytes used in <see cref="Bytes"/>.
            /// </summary>
            public int NumBytes;

            #region Fields for general use

            /// <summary>
            /// The connection the data is for.
            /// </summary>
            public ConnData? Conn;

            /// <summary>
            /// Flags for data to be sent.
            /// </summary>
            public NetSendFlags SendFlags;

            /// <summary>
            /// Flags for received data.
            /// </summary>
            public NetReceiveFlags ReceiveFlags;

            #endregion

            #region Fields for outgoing reliable data

            /// <summary>
            /// Timestamp that the packet was last sent.
            /// </summary>
            public long? LastTryTimestamp;

            /// <summary>
            /// The number of times the data was sent.
            /// </summary>
            public byte Tries;

            /// <summary>
            /// A linked list of callbacks to invoke when the send is complete (successfully sent and ACK'd or cancelled).
            /// </summary>
            public IReliableCallbackInvoker? CallbackInvoker;

            #endregion

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    Array.Clear(Bytes, 0, Bytes.Length);
                    NumBytes = 0;

                    Conn = null;
                    SendFlags = NetSendFlags.None;
                    ReceiveFlags = NetReceiveFlags.None;

                    LastTryTimestamp = null;
                    Tries = 0;

                    if (CallbackInvoker is not null)
                    {
                        CallbackInvoker.Dispose();
                        CallbackInvoker = null;
                    }
                }

                base.Dispose(isDisposing); // returns this object to its pool
            }
        }

        /// <summary>
        /// Enum that represents how a reliable callback is to be invoked.
        /// </summary>
        private enum ReliableCallbackExecutionOption
        {
            /// <summary>
            /// The callback is to be executed asynchronously on the mainloop thread.
            /// </summary>
            Mainloop = 0,

            /// <summary>
            /// The callback is to be executed synchronously on the thread that is processing the callback.
            /// </summary>
            Synchronous,

            /// <summary>
            /// The callback is to be executed asynchronously on a thread from the thread pool.
            /// </summary>
            ThreadPool,
        }

        /// <summary>
        /// Interface for an object that represents a callback for when a request to send reliable data has completed (successfully or not).
        /// </summary>
        /// <remarks>
        /// A successful send occurs when an an ACK (0x00 0x04) is received.
        /// An unsuccessful send occurs when the connection is disconnected before an ACK is received.
        /// </remarks>
        private interface IReliableCallbackInvoker : IDisposable
        {
            /// <summary>
            /// Invokes the reliable callback.
            /// </summary>
            /// <param name="connData">Data about the connection.</param>
            /// <param name="success">True if the reliable packet was successfully sent and acknowledged. False if it was cancelled.</param>
            void Invoke(ConnData connData, bool success);

            /// <summary>
            /// Option that indicates how the callback should be invoked.
            /// </summary>
            ReliableCallbackExecutionOption ExecutionOption { get; }

            /// <summary>
            /// The next reliable callback to invoke in a chain forming linked list.
            /// This is used for reliable packets that have been grouped.
            /// </summary>
            IReliableCallbackInvoker? Next
            {
                get;
                set;
            }
        }

        /// <summary>
        /// A reliable callback invoker that wraps calling a delegate for a <see cref="ConnData"/>.
        /// </summary>
        private class ReliableConnectionCallbackInvoker : PooledObject, IReliableCallbackInvoker
        {
            public delegate void ReliableConnectionCallback(ConnData connData, bool success);
            private ReliableConnectionCallback? _callback;

            public void SetCallback(ReliableConnectionCallback callback, ReliableCallbackExecutionOption executionOption)
            {
                if (!Enum.IsDefined(executionOption))
                    throw new InvalidEnumArgumentException(nameof(executionOption), (int)executionOption, typeof(ReliableCallbackExecutionOption));

                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
                ExecutionOption = executionOption;
            }

            #region IReliableCallbackInvoker

            public void Invoke(ConnData connData, bool success)
            {
                _callback?.Invoke(connData, success);
            }

            public ReliableCallbackExecutionOption ExecutionOption { get; private set; } = ReliableCallbackExecutionOption.Mainloop;

            public IReliableCallbackInvoker? Next { get; set; }

            #endregion

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    _callback = null;
                    ExecutionOption = ReliableCallbackExecutionOption.Mainloop;
                    Next = null;
                }

                base.Dispose(isDisposing);
            }
        }

        /// <summary>
        /// A reliable callback invoker that wraps calling a delegate for a <see cref="Player"/>.
        /// </summary>
        private class ReliablePlayerCallbackInvoker : PooledObject, IReliableCallbackInvoker
        {
            private ReliableDelegate? _callback;

            public void SetCallback(ReliableDelegate callback, ReliableCallbackExecutionOption executionOption)
            {
                if (!Enum.IsDefined(executionOption))
                    throw new InvalidEnumArgumentException(nameof(executionOption), (int)executionOption, typeof(ReliableCallbackExecutionOption));

                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
                ExecutionOption = executionOption;
            }

            #region IReliableDelegateInvoker Members

            public void Invoke(ConnData connData, bool success)
            {
                if (connData is PlayerConnection playerConnection)
                {
                    Player? player = playerConnection.Player;
                    if (player is not null)
                    {
                        _callback?.Invoke(player, success);
                    }
                }
            }

            public ReliableCallbackExecutionOption ExecutionOption { get; private set; } = ReliableCallbackExecutionOption.Mainloop;

            public IReliableCallbackInvoker? Next { get; set; }

            #endregion

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    _callback = null;
                    ExecutionOption = ReliableCallbackExecutionOption.Mainloop;
                    Next = null;
                }

                base.Dispose(isDisposing);
            }
        }

        /// <summary>
        /// A reliable callback invoker that wraps calling a delegate for a <see cref="Player"/> with an extra state argument of type <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The type of state to pass to the callback.</typeparam>
        private class ReliablePlayerCallbackInvoker<T> : PooledObject, IReliableCallbackInvoker
        {
            private ReliableDelegate<T>? _callback;
            private T? _state;

            public void SetCallback(ReliableDelegate<T> callback, T state, ReliableCallbackExecutionOption executionOption)
            {
                if (!Enum.IsDefined(executionOption))
                    throw new InvalidEnumArgumentException(nameof(executionOption), (int)executionOption, typeof(ReliableCallbackExecutionOption));

                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
                _state = state;
                ExecutionOption = executionOption;
            }

            #region IReliableDelegateInvoker Members

            public void Invoke(ConnData connData, bool success)
            {
                if (connData is PlayerConnection playerConnection)
                {
                    Player? player = playerConnection.Player;
                    if (player is not null)
                    {
                        _callback?.Invoke(player, success, _state!);
                    }
                }
            }

            public ReliableCallbackExecutionOption ExecutionOption { get; private set; } = ReliableCallbackExecutionOption.Mainloop;

            public IReliableCallbackInvoker? Next { get; set; }

            #endregion

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    _callback = null;
                    _state = default;
                    ExecutionOption = ReliableCallbackExecutionOption.Mainloop;
                    Next = null;
                }

                base.Dispose(isDisposing);
            }
        }

        /// <summary>
        /// A reliable callback invoker that wraps calling a delegate for an <see cref="IClientConnection"/>.
        /// </summary>
        private class ReliableClientConnectionCallbackInvoker : PooledObject, IReliableCallbackInvoker
        {
            private ClientReliableCallback? _callback;

            public void SetCallback(ClientReliableCallback callback, ReliableCallbackExecutionOption executionOption)
            {
                if (!Enum.IsDefined(executionOption))
                    throw new InvalidEnumArgumentException(nameof(executionOption), (int)executionOption, typeof(ReliableCallbackExecutionOption));

                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
                ExecutionOption = executionOption;
            }

            #region IReliableDelegateInvoker Members

            public void Invoke(ConnData connData, bool success)
            {
                if (connData is IClientConnection clientConnection)
                {
                    _callback?.Invoke(clientConnection, success);
                }
            }

            public ReliableCallbackExecutionOption ExecutionOption { get; private set; } = ReliableCallbackExecutionOption.Mainloop;

            public IReliableCallbackInvoker? Next { get; set; }

            #endregion

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    _callback = null;
                    ExecutionOption = ReliableCallbackExecutionOption.Mainloop;
                    Next = null;
                }

                base.Dispose(isDisposing);
            }
        }

        /// <summary>
        /// Interface for an object used to manage sending of sized data (0x00 0x0A).
        /// </summary>
        private interface ISizedSendData
        {
            /// <summary>
            /// Total # of bytes of data to send.
            /// </summary>
            int TotalLength { get; }

            /// <summary>
            /// The # of bytes remaining to send.
            /// </summary>
            int Remaining { get; }

            /// <summary>
            /// Requests the sender to provide data.
            /// </summary>
            /// <param name="dataSpan">The buffer to fill. An empty buffer indicates that the send is finished (completed or cancelled).</param>
            void RequestData(Span<byte> dataSpan);

            bool IsCancellationRequested { get; }

            bool IsCancellationRequestedByConnection { get; }

            void Cancel(bool isRequestedByConnection);

            /// <summary>
            /// Returns the <see cref="ISizedSendData"/> object to its pool to be reused.
            /// </summary>
            void Return();
        }

        /// <summary>
        /// Helper that assists in the retrieval of data for a sized send (0x00 0x0A).
        /// </summary>
        /// <remarks>
        /// This class wraps calling the delegate provided to retrieve data and keeps track of the current position.
        /// It also provides a mechanism to request that the sized send be cancelled.
        /// </remarks>
        /// <typeparam name="TState">The type of state to pass to the delegate when requesting data.</typeparam>
        /// <param name="requestDataCallback">The delegate for requesting data.</param>
        /// <param name="state">The state to pass when calling the <paramref name="requestDataCallback"/>.</param>
        /// <param name="totalLength">The total # of bytes to be sent.</param>
        private class SizedSendData<TState> : ISizedSendData, IResettable
        {
            public static readonly ObjectPool<SizedSendData<TState>> Pool = new DefaultObjectPool<SizedSendData<TState>>(new DefaultPooledObjectPolicy<SizedSendData<TState>>(), Constants.TargetPlayerCount * 2);

            private GetSizedSendDataDelegate<TState>? _requestDataCallback;
            private TState? _state;
            private int _totalLength;
            private int _offset;
            private bool _isCancellationRequested;
            private bool _isCancellationRequestedByConnection;

            public void Initialize(GetSizedSendDataDelegate<TState> requestDataCallback, TState state, int totalLength)
            {
                if (_requestDataCallback is not null)
                    throw new InvalidOperationException("Already initialized. It needs to be reset before reusing.");

                _requestDataCallback = requestDataCallback ?? throw new ArgumentNullException(nameof(requestDataCallback));
                _state = state;
                _totalLength = totalLength;
                _offset = 0;
            }

            #region ISizedSendData

            int ISizedSendData.TotalLength => _totalLength;

            int ISizedSendData.Remaining => _totalLength - _offset;

            void ISizedSendData.RequestData(Span<byte> dataSpan)
            {
                Debug.Assert(dataSpan.Length <= ((ISizedSendData)this).Remaining);

                _requestDataCallback?.Invoke(_state!, _offset, dataSpan);
                _offset += dataSpan.Length;
            }

            bool ISizedSendData.IsCancellationRequested => _isCancellationRequested;

            bool ISizedSendData.IsCancellationRequestedByConnection => _isCancellationRequestedByConnection;

            void ISizedSendData.Cancel(bool isRequestedByConnection)
            {
                _isCancellationRequested = true;
                _isCancellationRequestedByConnection = isRequestedByConnection;
            }

            void ISizedSendData.Return()
            {
                Pool.Return(this);
            }

            #endregion

            #region IResettable

            public bool TryReset()
            {
                _requestDataCallback = null;
                _state = default;
                _totalLength = 0;
                _offset = 0;
                _isCancellationRequested = false;
                _isCancellationRequestedByConnection = false;
                return true;
            }

            #endregion
        }

        /// <summary>
        /// Helper for receiving big data streams. 
        /// It is used to accumulate data by appending each piece as it is received.
        /// The data is stored in memory, using arrays rented from the default <see cref="ArrayPool{Byte}"/>.
        /// A maximum of <see cref="Constants.MaxBigPacket"/> bytes are allowed to be stored. 
        /// Attempting to store beyond the limit sets the <see cref="IsOverflow"/> indicator.
        /// </summary>
        /// <remarks>
        /// Big data is a mechanism in the 'core' Subspace protocol.
        /// Big data consists of zero or more 0x00 0x08 packets followed by a single 0x00 0x09 packet indicating the end.
        /// These packets are sent reliably and therefore are processed in order, effectively being a stream.
        /// </remarks>
        private class BigReceive : PooledObject
        {
            private byte[]? _buffer;

            /// <summary>
            /// The entire buffer, including bytes past <see cref="Length"/> that are not set.
            /// </summary>
            /// <remarks>
            /// Empty when <see cref="IsOverflow"/>.
            /// Otherwise, guaranteed to be at least <see cref="Constants.MaxPacket"/> in length.
            /// </remarks>
            public Span<byte> Buffer => _buffer;

            /// <summary>
            /// Flags that indicate how the data was received for the transfer.
            /// </summary>
            public NetReceiveFlags Flags { get; private set; } = NetReceiveFlags.Big;

            /// <summary>
            /// The # of bytes received so far for the transfer.
            /// </summary>
            /// <remarks>
            /// When <see cref="IsOverflow"/> is true, it still keeps track even though the data is not held.
            /// </remarks>
            public int Length { get; private set; } = 0;

            /// <summary>
            /// Indicates whether the transfer is larger than the allowed <see cref="Constants.MaxBigPacket"/>.
            /// </summary>
            public bool IsOverflow { get; private set; } = false;

            /// <summary>
            /// Appends data for the transfer.
            /// </summary>
            /// <param name="data">The data to append.</param>
            /// <param name="flags">The flags to append.</param>
            /// <returns>Whether the transfer is in the <see cref="IsOverflow"/> state.</returns>
            public bool Append(ReadOnlySpan<byte> data, NetReceiveFlags flags)
            {
                int newLength = Length + data.Length;

                if (newLength > Constants.MaxBigPacket)
                {
                    IsOverflow = true;

                    // Since it overflowed, there's no need to hold onto the buffer.
                    if (_buffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(_buffer, true);
                        _buffer = null;
                    }
                }
                else
                {
                    if (_buffer is null)
                    {
                        // This is the first piece of data.
                        // We can safely assume there will be more. Otherwise, there would be no reason it's being sent as big data.
                        // Start with extra capacity, to reduce the likelihood that it will need to be reallocated and copied.
                        const int MinStartingSize = Constants.MaxPacket * 8;
                        _buffer = ArrayPool<byte>.Shared.Rent(int.Max(newLength, MinStartingSize));
                    }
                    else if (_buffer.Length < newLength)
                    {
                        // The buffer does not have enough capacity. Replace it with a larger one.
                        byte[] newBuffer = ArrayPool<byte>.Shared.Rent(int.Min(newLength * 2, Constants.MaxBigPacket));
                        _buffer.AsSpan(0, Length).CopyTo(newBuffer);
                        ArrayPool<byte>.Shared.Return(_buffer, true);
                        _buffer = newBuffer;
                    }

                    data.CopyTo(_buffer.AsSpan(Length));
                }

                Length = newLength;
                Flags |= flags;

                return IsOverflow;
            }

            /// <summary>
            /// Resets the object back to its initial state of having been given no data.
            /// </summary>
            public void Reset()
            {
                Flags = NetReceiveFlags.Big;

                if (_buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(_buffer, true);
                    _buffer = null;
                }

                Length = 0;
                IsOverflow = false;
            }

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    Reset();
                }

                base.Dispose(isDisposing);
            }
        }

        /// <summary>
        /// Options in the ASSS Ping/Information Protocol.
        /// </summary>
        /// <remarks>
        /// https://bitbucket.org/grelminar/asss/src/master/doc/ping.txt
        /// </remarks>
        [Flags]
        private enum PingOptionFlags
        {
            /// <summary>
            /// No information.
            /// </summary>
            None = 0,

            /// <summary>
            /// Global player count information.
            /// </summary>
            GlobalSummary = 0x01,

            /// <summary>
            /// By-arena player count information.
            /// </summary>
            ArenaSummary = 0x02,
        }

        /// <summary>
        /// Represents how the server should respond to pings.
        /// </summary>
        [Flags]
        private enum PingPopulationMode
        {
            /// <summary>
            /// Send the total player count.
            /// </summary>
            Total = 1,

            /// <summary>
            /// Send the playing count (# of players in ships, not spectating).
            /// </summary>
            Playing = 2,
        }

        /// <summary>
        /// Configuration settings used by the <see cref="Network"/> module.
        /// </summary>
        private class Config
        {
            /// <summary>
            /// How long to get no data from a client before disconnecting him.
            /// </summary>
            [ConfigHelp<int>("Net", "DropTimeout", ConfigScope.Global, Default = 3000,
                Description = "How long to get no data from a client before disconnecting him (in ticks).")]
            public TimeSpan DropTimeout { get; private set; }

            /// <summary>
            /// How many S2C packets the server will buffer for a client before dropping him.
            /// </summary>
            [ConfigHelp<int>("Net", "MaxOutlistSize", ConfigScope.Global, Default = 500,
                Description = "How many S2C packets the server will buffer for a client before dropping him.")]
            public int MaxOutlistSize { get; private set; }

            /// <summary>
            /// How long after not having sent data to a player connection, to send a <see cref="Packets.Game.S2CPacketType.KeepAlive"/> packet (game protocol).
            /// </summary>
            public TimeSpan PlayerKeepAliveThreshold { get; private set; }

            /// <summary>
            /// The maximum number of incoming reliable packets to buffer for a player connection.
            /// </summary>
            public int PlayerReliableReceiveWindowSize { get; private set; }

            /// <summary>
            /// The maximum number of incoming reliable packets to buffer for a client connection.
            /// </summary>
            public int ClientConnectionReliableReceiveWindowSize { get; private set; }

            /// <summary>
            /// if we haven't sent a reliable packet after this many tries, drop the connection
            /// </summary>
            public int MaxRetries { get; private set; }

            /// <summary>
            /// Whether to limit the size of grouped reliable packets to be able to fit into another grouped packet.
            /// </summary>
            public bool LimitReliableGroupingSize { get; private set; }

            /// <summary>
            /// When sending sized data, the # of queued packets at which an additional batch should not be queued.
            /// </summary>
            public int SizedQueueThreshold { get; private set; }

            /// <summary>
            /// When sending sized data, the maximum # of packets to queue up in a batch.
            /// </summary>
            /// <remarks>
            /// This means, the maximum # of packets that can be queued at the same time is <see cref="SizedQueueThreshold"/> - 1 + <see cref="SizedQueuePackets"/>.
            /// </remarks>
            public int SizedQueuePackets { get; private set; }

            /// <summary>
            /// When sending sized data, whether to attempt to send outgoing reliable data immediately after queuing sized data.
            /// </summary>
            public bool SizedSendOutgoing { get; private set; }

            /// <summary>
            /// ip/udp overhead, in bytes per physical packet
            /// </summary>
            public int PerPacketOverhead { get; private set; }

            /// <summary>
            /// How often to refresh the ping packet data.
            /// </summary>
            public TimeSpan PingRefreshThreshold { get; private set; }

            /// <summary>
            /// Controls the way <see cref="SendThread"/> waits between each iteration.
            /// </summary>
            /// <remarks>
            /// <para>
            /// Thread.Sleep(1) will actually wait about 1 ms on Linux.
            /// </para>
            /// <para>
            /// On Windows, Thread.Sleep(1) will wait at least 15.625 ms (1000/64) by default.
            /// However, it can be modified using the timeBeginPeriod function API in timeapi.h
            /// which should have been called by the executable.
            /// </para>
            /// <para>
            /// This purposely undocumented setting gives further control on how a wait is performed, just in case it's needed.
            /// </para>
            /// </remarks>
            public SendThreadWaitOption SendThreadWaitOption { get; private set; }

            /// <summary>
            /// Display total or playing in simple ping responses.
            /// </summary>
            [ConfigHelp<PingPopulationMode>("Net", "SimplePingPopulationMode", ConfigScope.Global, Default = PingPopulationMode.Total,
                Description = """
                    Which value to return in the simple ping response (Subspace and Continuum clients).
                    1 = Total player count (default);
                    2 = # of players playing (in ships);
                    3 = Alternate between Total and Playing
                    """)]
            public PingPopulationMode SimplePingPopulationMode { get; private set; }

            public void Load(IConfigManager configManager)
            {
                ArgumentNullException.ThrowIfNull(configManager);

                DropTimeout = TimeSpan.FromMilliseconds(configManager.GetInt(configManager.Global, "Net", "DropTimeout", NetSettings.DropTimeout.Default) * 10);
                MaxOutlistSize = configManager.GetInt(configManager.Global, "Net", "MaxOutlistSize", NetSettings.MaxOutlistSize.Default);
                SimplePingPopulationMode = configManager.GetEnum(configManager.Global, "Net", "SimplePingPopulationMode", PingPopulationMode.Total);

                // (deliberately) undocumented settings
                PlayerKeepAliveThreshold = TimeSpan.FromMilliseconds(10 * configManager.GetInt(configManager.Global, "Net", "PlayerKeepAliveThreshold", 500));
                PlayerReliableReceiveWindowSize = configManager.GetInt(configManager.Global, "Net", "PlayerReliableReceiveWindowSize", Constants.PlayerReliableReceiveWindowSize);
                ClientConnectionReliableReceiveWindowSize = configManager.GetInt(configManager.Global, "Net", "ClientConnectionReliableReceiveWindowSize", Constants.ClientConnectionReliableReceiveWindowSize);
                MaxRetries = configManager.GetInt(configManager.Global, "Net", "MaxRetries", 15);
                SizedQueueThreshold = int.Clamp(configManager.GetInt(configManager.Global, "Net", "PresizedQueueThreshold", 5), 1, MaxOutlistSize);
                SizedQueuePackets = int.Clamp(configManager.GetInt(configManager.Global, "Net", "PresizedQueuePackets", 25), 1, MaxOutlistSize);
                SizedSendOutgoing = configManager.GetInt(configManager.Global, "Net", "SizedSendOutgoing", 0) != 0;
                LimitReliableGroupingSize = configManager.GetInt(configManager.Global, "Net", "LimitReliableGroupingSize", 0) != 0;
                PerPacketOverhead = configManager.GetInt(configManager.Global, "Net", "PerPacketOverhead", 28);
                PingRefreshThreshold = TimeSpan.FromMilliseconds(10 * configManager.GetInt(configManager.Global, "Net", "PingDataRefreshTime", 200));
                SendThreadWaitOption = configManager.GetEnum(configManager.Global, "Net", "SendThreadWaitOption", SendThreadWaitOption.Sleep);
            }
        }

        private enum SendThreadWaitOption
        {
            Sleep,
            BusyWait,
            SpinWait,
        }

        /// <summary>
        /// Helper that holds population stats that are calculated for responding to pings.
        /// </summary>
        private class PopulationStats
        {
            /// <summary>
            /// Total # of players.
            /// </summary>
            private uint _total = 0;

            /// <summary>
            /// # of players playing (in ships).
            /// </summary>
            private uint _playing = 0;

            /// <summary>
            /// For synchronizing <see cref="_total"/> and <see cref="_playing"/>.
            /// </summary>
            private readonly Lock _lock = new();

            #region ReceiveThread only

            public uint TempTotal = 0;
            public uint TempPlaying = 0;

            #endregion

            public void GetStats(out uint total, out uint playing)
            {
                lock (_lock)
                {
                    total = _total;
                    playing = _playing;
                }
            }

            public void SetStats(uint total, uint playing)
            {
                lock (_lock)
                {
                    _total = total;
                    _playing = playing;
                }
            }
        }

        /// <summary>
        /// Helper for managing data used for responding to pings.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The <see cref="ConnectAsPopulationStats"/> dictionary is accessed by multiple threads.
        /// Though only the mainloop thread mutates the dictionary (during Load and Unload), 
        /// and at those times nothing else should be accessing it.
        /// Therefore, no need to do synchronization (locks or change to ConcurrentDictionary).
        /// Also, the PopulationStats class itself handles synchronization of its own data.
        /// </para>
        /// <para>
        /// All other data in this class is accessed only by the <see cref="ReceiveThread"/>.
        /// </para>
        /// </remarks>
        private class PingData
        {
            /// <summary>
            /// The timestamp of the last data refresh.
            /// </summary>
            public DateTime? LastRefresh = null;

            /// <summary>
            /// Global, zone-wide population stats.
            /// </summary>
            public readonly PopulationStats Global = new();

            /// <summary>
            /// Population stats for ConnectAs endpoints.
            /// </summary>
            /// <remarks>
            /// Key: ConnectAs name
            /// </remarks>
            public readonly Dictionary<string, PopulationStats> ConnectAsPopulationStats = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Buffer for the Arena Summary data for the ASSS ping/info protocol which contains population data for each arena.
            /// </summary>
            public readonly byte[] ArenaSummaryBytes = new byte[Constants.MaxPacket - 16];

            /// <summary>
            /// Used length of <see cref="ArenaSummaryBytes"/>.
            /// </summary>
            public int ArenaSummaryLength = 0;

            public void Clear()
            {
                ConnectAsPopulationStats.Clear();
            }
        }

        /// <summary>
        /// Various stats about network operations.
        /// </summary>
        private class NetStats : IReadOnlyNetStats
        {
            public ulong PingsReceived, PacketsSent, PacketsReceived;
            public ulong BytesSent, BytesReceived;
            public ulong BuffersTotal, BuffersUsed;
            public readonly ulong[] GroupedStats = new ulong[8];
            public readonly ulong[] RelGroupedStats = new ulong[8];
            public readonly ulong[] PriorityStats = new ulong[(int)Enum.GetValues<BandwidthPriority>().Max() + 1];

            #region IReadOnlyNetStats

            ulong IReadOnlyNetStats.PingsReceived => Interlocked.Read(ref PingsReceived);
            ulong IReadOnlyNetStats.PacketsSent => Interlocked.Read(ref PacketsSent);
            ulong IReadOnlyNetStats.PacketsReceived => Interlocked.Read(ref PacketsReceived);
            ulong IReadOnlyNetStats.BytesSent => Interlocked.Read(ref BytesSent);
            ulong IReadOnlyNetStats.BytesReceived => Interlocked.Read(ref BytesReceived);
            ulong IReadOnlyNetStats.BuffersTotal => Interlocked.Read(ref BuffersTotal);
            ulong IReadOnlyNetStats.BuffersUsed => Interlocked.Read(ref BuffersUsed);

            ulong IReadOnlyNetStats.GroupedStats0 => Interlocked.Read(ref GroupedStats[0]);
            ulong IReadOnlyNetStats.GroupedStats1 => Interlocked.Read(ref GroupedStats[1]);
            ulong IReadOnlyNetStats.GroupedStats2 => Interlocked.Read(ref GroupedStats[2]);
            ulong IReadOnlyNetStats.GroupedStats3 => Interlocked.Read(ref GroupedStats[3]);
            ulong IReadOnlyNetStats.GroupedStats4 => Interlocked.Read(ref GroupedStats[4]);
            ulong IReadOnlyNetStats.GroupedStats5 => Interlocked.Read(ref GroupedStats[5]);
            ulong IReadOnlyNetStats.GroupedStats6 => Interlocked.Read(ref GroupedStats[6]);
            ulong IReadOnlyNetStats.GroupedStats7 => Interlocked.Read(ref GroupedStats[7]);

            ulong IReadOnlyNetStats.RelGroupedStats0 => Interlocked.Read(ref RelGroupedStats[0]);
            ulong IReadOnlyNetStats.RelGroupedStats1 => Interlocked.Read(ref RelGroupedStats[1]);
            ulong IReadOnlyNetStats.RelGroupedStats2 => Interlocked.Read(ref RelGroupedStats[2]);
            ulong IReadOnlyNetStats.RelGroupedStats3 => Interlocked.Read(ref RelGroupedStats[3]);
            ulong IReadOnlyNetStats.RelGroupedStats4 => Interlocked.Read(ref RelGroupedStats[4]);
            ulong IReadOnlyNetStats.RelGroupedStats5 => Interlocked.Read(ref RelGroupedStats[5]);
            ulong IReadOnlyNetStats.RelGroupedStats6 => Interlocked.Read(ref RelGroupedStats[6]);
            ulong IReadOnlyNetStats.RelGroupedStats7 => Interlocked.Read(ref RelGroupedStats[7]);

            ulong IReadOnlyNetStats.PriorityStats0 => Interlocked.Read(ref PriorityStats[0]);
            ulong IReadOnlyNetStats.PriorityStats1 => Interlocked.Read(ref PriorityStats[1]);
            ulong IReadOnlyNetStats.PriorityStats2 => Interlocked.Read(ref PriorityStats[2]);
            ulong IReadOnlyNetStats.PriorityStats3 => Interlocked.Read(ref PriorityStats[3]);
            ulong IReadOnlyNetStats.PriorityStats4 => Interlocked.Read(ref PriorityStats[4]);

            #endregion
        }

        /// <summary>
        /// A helper for grouping up packets to be sent out together as a single combined (0x00 0x0E) packet.
        /// </summary>
        private ref struct PacketGrouper
        {
            private readonly Network _network;
            private readonly Span<byte> _bufferSpan;
            private Span<byte> _remainingSpan;
            public readonly int RemainingLength => _remainingSpan.Length;
            private int _count;
            public readonly int Count => _count;
            private int _numBytes;
            public readonly int NumBytes => _numBytes;

            public PacketGrouper(Network network, Span<byte> bufferSpan)
            {
                if (bufferSpan.Length < 4)
                    throw new ArgumentException("Needs a minimum length of 4 bytes.", nameof(bufferSpan));

                _network = network ?? throw new ArgumentNullException(nameof(network));
                _bufferSpan = bufferSpan;

                _bufferSpan[0] = 0x00;
                _bufferSpan[1] = 0x0E;
                _remainingSpan = _bufferSpan[2..Math.Min(bufferSpan.Length, Constants.MaxGroupedPacketLength)];
                _count = 0;
                _numBytes = 2;
            }

            public void Initialize()
            {
                _remainingSpan = _bufferSpan[2..Math.Min(_bufferSpan.Length, Constants.MaxGroupedPacketLength)];
                _count = 0;
                _numBytes = 2;
            }

            public void Flush(ConnData conn)
            {
                ArgumentNullException.ThrowIfNull(conn);

                if (_count == 1)
                {
                    // there's only one in the group, so don't send it in a group. 
                    // +3 to skip past the 00 0E and size of first packet
                    _network.SendRaw(conn, _bufferSpan[3.._numBytes]);
                }
                else if (_count > 1)
                {
                    _network.SendRaw(conn, _bufferSpan[.._numBytes]);
                }

                // record stats about grouped packets
                if (_count > 0)
                {
                    Interlocked.Increment(ref _network._globalStats.GroupedStats[Math.Min((_count - 1), _network._globalStats.GroupedStats.Length - 1)]);
                }

                Initialize();
            }

            /// <summary>
            /// Checks whether a specified length of data can be appended.
            /// </summary>
            /// <param name="length">The # of bytes to check for.</param>
            /// <returns>True if the data can be appended. Otherwise, false.</returns>
            public readonly bool CheckAppend(int length)
            {
                if (length > Constants.MaxGroupedPacketItemLength)
                    return false;

                return _remainingSpan.Length >= length + 1; // +1 is for the byte that specifies the length
            }

            public bool TryAppend(ReadOnlySpan<byte> data)
            {
                if (data.Length == 0)
                    throw new ArgumentOutOfRangeException(nameof(data), "Length must be > 0.");

                if (data.Length > Constants.MaxGroupedPacketItemLength)
                    return false;

                int lengthWithHeader = data.Length + 1; // +1 is for the byte that specifies the length
                if (_remainingSpan.Length < lengthWithHeader)
                    return false;

                _remainingSpan[0] = (byte)data.Length;
                data.CopyTo(_remainingSpan[1..]);

                _remainingSpan = _remainingSpan[lengthWithHeader..];
                _numBytes += lengthWithHeader;
                _count++;

                return true;
            }

            public void Send(SubspaceBuffer bufferToSend, ConnData conn)
            {
                ArgumentNullException.ThrowIfNull(bufferToSend);
                ArgumentNullException.ThrowIfNull(conn);

                if (bufferToSend.NumBytes <= 0)
                    throw new ArgumentException("At least one byte of data is required.", nameof(bufferToSend));

                Send(new ReadOnlySpan<byte>(bufferToSend.Bytes, 0, bufferToSend.NumBytes), conn);
            }

            public void Send(ReadOnlySpan<byte> data, ConnData conn)
            {
                if (data.Length <= 0)
                    throw new ArgumentException("At least one byte of data is required.", nameof(data));

                ArgumentNullException.ThrowIfNull(conn);

#if !DISABLE_GROUPED_SEND
                if (data.Length <= Constants.MaxGroupedPacketItemLength) // 255 is the size limit a grouped packet can store (max 1 byte can represent for the length)
                {
                    // TODO: Find out why ASSS subtracts 10 (MAXPACKET - 10 - buf->len).  For now, ignoring that and just ensuring the data fits.
                    int lengthWithHeader = data.Length + 1; // +1 is for the byte that specifies the length
                    if (_remainingSpan.Length < lengthWithHeader)
                        Flush(conn); // not enough room in the grouped packet, send it out first, to start with a fresh grouped packet

                    _remainingSpan[0] = (byte)data.Length;
                    data.CopyTo(_remainingSpan[1..]);

                    _remainingSpan = _remainingSpan[lengthWithHeader..];
                    _numBytes += lengthWithHeader;
                    _count++;
                    return;
                }
#endif
                // can't fit into a grouped packet, send immediately
                _network.SendRaw(conn, data);

                Interlocked.Increment(ref _network._globalStats.GroupedStats[0]);
            }
        }

        /// <summary>
        /// State for executing a reliable callback.
        /// </summary>
        private readonly struct InvokeReliableCallbackWork
        {
            public required readonly IReliableCallbackInvoker CallbackInvoker { get; init; }
            public required readonly ConnData ConnData { get; init; }
            public required readonly bool Success { get; init; }
        }

        /// <summary>
        /// State for executing processing of big data (sent using the the 0x00 0x08 and 0x00 0x09 mechanism) on the mainloop thread.
        /// </summary>
        private readonly struct BigPacketWork(ConnData connData, BigReceive bigReceive)
        {
            public readonly ConnData ConnData = connData;
            public readonly BigReceive BigReceive = bigReceive;
        }

        /// <summary>
        /// A queue implemented as a circular buffer in which items are ordered by a sequence number.
        /// Items can be added out of order, but can only be processed in the intended sequence.
        /// </summary>
        /// <typeparam name="T">The type of items in the queue.</typeparam>
        public class CircularSequenceQueue<T> where T : class
        {
            private readonly T?[] _items;
            private int _currentSequenceNum = 0;
            private int _currentIndex = 0;

            public CircularSequenceQueue(int capacity)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
                _items = new T[capacity];
            }

            public int Capacity => _items.Length;
            public int CurrentSequenceNum => _currentSequenceNum;

            public bool TryAdd(int sequenceNum, T value, out bool isDuplicate)
            {
                int offset = -_currentSequenceNum;
                int offsetSequenceNum = sequenceNum + offset;

                if (offsetSequenceNum >= 0 && offsetSequenceNum < _items.Length)
                {
                    // sequenceNum is in the range of the buffer.
                    int i = (_currentIndex + offsetSequenceNum) % _items.Length;

                    if (_items[i] is not null)
                    {
                        // Already have an item, consider it a dup.
                        isDuplicate = true;
                        return false;
                    }

                    _items[i] = value;
                    isDuplicate = false;
                    return true;
                }
                else
                {
                    // sequenceNum is not in the range of the buffer.
                    isDuplicate = (offsetSequenceNum < 0);
                    return false;
                }
            }

            public bool TryGetNext(out int sequenceNum, [MaybeNullWhen(false)] out T value)
            {
                value = _items[_currentIndex];
                if (value is null)
                {
                    sequenceNum = 0;
                    return false;
                }

                // Got an item
                sequenceNum = _currentSequenceNum;
                _items[_currentIndex] = null;

                // Advance to the next
                _currentIndex = (_currentIndex + 1) % _items.Length;
                _currentSequenceNum++;

                return true;
            }

            public bool HasNext()
            {
                return _items[_currentIndex] is not null;
            }

            public void Reset()
            {
                for (int i = 0; i < _items.Length; i++)
                {
                    T? item = _items[i];
                    if (item is not null)
                    {
                        _items[i] = null;

                        if (item is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }
                }

                _currentSequenceNum = 0;
                _currentIndex = 0;
            }
        }

        #endregion
    }
}

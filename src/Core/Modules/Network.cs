using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Packets;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality to communicate using UDP and the Subspace 'core' procotol.
    /// 
    /// <para>
    /// Though primarily designed towards being used as a server, this module also provides functionality for use as a client.
    /// The <see cref="INetwork"/> interface provides methods for server functionality,
    /// and the <see cref="INetworkClient"/> interface provides functionality for being a client.
    /// As a 'zone' server it acts as a server that listens for 'game' clients to connect.
    /// However, it may act as a client to other servers, such as a 'billing' server (<see cref="BillingUdp"/>).
    /// </para>
    /// 
    /// <para>
    /// This module is a direct port of the 'net' module from ASSS.
    /// One notable difference is the addition of the ability to group reliable data.
    /// That is, when there are multiple packets waiting to be sent reliably to a connection, it can combine them (size permitting)
    /// into a grouped packet (0x00 0x0E) wrapped within a single reliable packet (0x00 0x03).
    /// So, instead of using multiple reliable packets (each with a sequence number), it can instead use a single reliable packet (1 sequence number).
    /// Doing so reduces bandwidth used: fewer ACK packets (0x00 0x04) used and likely fewer datagrams too (ACKs are most likely sent separately, not grouped).
    /// </para>
    /// </summary>
    [CoreModuleInfo]
    public sealed class Network : IModule, IModuleLoaderAware, INetwork, INetworkEncryption, INetworkClient, IDisposable
    {
        private ComponentBroker _broker;
        private IBandwidthLimiterProvider _bandwithLimiterProvider;
        private IConfigManager _configManager;
        private ILagCollect _lagCollect;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IMainloopTimer _mainloopTimer;
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IPrng _prng;
        private InterfaceRegistrationToken<INetwork> _iNetworkToken;
        private InterfaceRegistrationToken<INetworkClient> _iNetworkClientToken;
        private InterfaceRegistrationToken<INetworkEncryption> _iNetworkEncryptionToken;

        private Pool<SubspaceBuffer> _bufferPool;
        private Pool<BigReceive> _bigReceivePool;
        private Pool<ReliableCallbackInvoker> _reliableCallbackInvokerPool;
        private NonTransientObjectPool<LinkedListNode<SubspaceBuffer>> _bufferNodePool;

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
        private PlayerDataKey<ConnData> _connKey;

        /// <summary>
        /// Dictionary of known player connections.
        /// </summary>
        /// <remarks>
        /// Key = EndPoint (IP + port)
        /// Value = Player
        /// </remarks>
        private readonly ConcurrentDictionary<EndPoint, Player> _clientDictionary = new();

        /// <summary>
        /// Dictionary of active client connections.
        /// </summary>
        /// <remarks>
        /// Synchronized with <see cref="_clientConnectionsLock"/>.
        /// </remarks>
        private readonly Dictionary<EndPoint, NetClientConnection> _clientConnections = new();
        private readonly ReaderWriterLockSlim _clientConnectionsLock = new(LockRecursionPolicy.NoRecursion);

        private delegate void CorePacketHandler(ReadOnlySpan<byte> data, ConnData conn);

        /// <summary>
        /// Handlers for 'core' packets (ss protocol's network/transport layer).
        /// </summary>
        /// <remarks>
        /// The first byte of these packets is 0x00.
        /// The second byte identifies the type and is the index into this array.
        /// </remarks>
        private readonly CorePacketHandler[] _oohandlers;

        private const int MAXTYPES = 64;

        /// <summary>
        /// Handlers for 'game' packets that are received.
        /// </summary>
        private readonly PacketDelegate[] _handlers = new PacketDelegate[MAXTYPES];

        /// <summary>
        /// Handlers for special network layer AKA 'core' packets that are received.
        /// </summary>
        private readonly PacketDelegate[] _nethandlers = new PacketDelegate[0x14];

        /// <summary>
        /// Handlers for sized packets (0x0A) that are received.
        /// </summary>
        private readonly SizedPacketDelegate[] _sizedhandlers = new SizedPacketDelegate[MAXTYPES];

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
        private readonly List<ConnectionInitHandler> _connectionInitHandlers = new();
        private readonly ReaderWriterLockSlim _connectionInitLock = new(LockRecursionPolicy.NoRecursion);

        private const int MICROSECONDS_PER_MILLISECOND = 1000;

        /// <summary>
        /// Queue that reliable threads watch for work.
        /// </summary>
        /// <remarks>
        /// When reliable data is received from known connection, that connection's <see cref="ConnData"/> 
        /// is added to this queue so that the reliable data can be processed.
        /// </remarks>
        private readonly MessagePassingQueue<ConnData> _relqueue = new();

        // Used for stopping the SendThread and ReceiveThread.
        private readonly CancellationTokenSource _stopCancellationTokenSource = new();
        private CancellationToken _stopToken;

        private readonly List<Thread> _threadList = new();
        private readonly List<Thread> _reliableThreads = new();

        /// <summary>
        /// List of info about the sockets being listened on.
        /// </summary>
        private readonly List<ListenData> _listenDataList = new();
        private readonly ReadOnlyCollection<ListenData> _readOnlyListenData;

        /// <summary>
        /// The socket for <see cref="ClientConnection"/>s.
        /// That is, for when this server is a client of another (e.g., to a billing server).
        /// </summary>
        private Socket _clientSocket;

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

        // TODO: Use a pinned array for sending too?
        // Socket sends can be done by:
        // - the send thread
        // - the receive thread (it also responds for core packets: sending ACKs, time sync response, responses from connection init handlers, etc...).
        // - the reliable thread(s) (it also responds for core packets)
        // Probably need a pool? Get one large byte[] and split it into multiple Memory<byte> of 512 bytes? Would need 2 + # of reliable threads
        //private readonly byte[] _receiveBuffer = GC.AllocateArray<byte>(length: 512*(2+reliableThreadCount), pinned: true);
        //private readonly ConcurrentBag<Memory<byte>> _sendBufferPool = 

        public Network()
        {
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

        public bool Load(
            ComponentBroker broker,
            IBandwidthLimiterProvider bandwidthLimiterProvider,
            IConfigManager configManager,
            ILagCollect lagCollect,
            ILogManager logManager,
            IMainloop mainloop,
            IMainloopTimer mainloopTimer,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData,
            IPrng prng)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _bandwithLimiterProvider = bandwidthLimiterProvider ?? throw new ArgumentNullException(nameof(bandwidthLimiterProvider));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _lagCollect = lagCollect ?? throw new ArgumentNullException(nameof(lagCollect));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));

            _config.Load(configManager);

            _connKey = _playerData.AllocatePlayerData<ConnData>();

            _bufferPool = objectPoolManager.GetPool<SubspaceBuffer>();
            _bigReceivePool = objectPoolManager.GetPool<BigReceive>();
            _reliableCallbackInvokerPool = objectPoolManager.GetPool<ReliableCallbackInvoker>();
            _bufferNodePool = new(new SubspaceBufferLinkedListNodePooledObjectPolicy());
            _objectPoolManager.TryAddTracked(_bufferNodePool);

            if (!InitializeSockets())
                return false;

            _stopToken = _stopCancellationTokenSource.Token;

            // receive thread
            Thread thread = new(ReceiveThread);
            thread.Name = "network-recv";
            thread.Start();
            _threadList.Add(thread);

            // send thread
            thread = new Thread(SendThread)
            {
                Name = "network-send"
            };
            thread.Start();
            _threadList.Add(thread);

            // reliable threads
            int reliableThreadCount = _configManager.GetInt(_configManager.Global, "Net", "ReliableThreads", 1);
            for (int i = 0; i < reliableThreadCount; i++)
            {
                thread = new Thread(RelThread)
                {
                    Name = "network-rel-" + i
                };
                thread.Start();
                _reliableThreads.Add(thread);
                _threadList.Add(thread);
            }

            // queue more data thread (sends sized data)
            _mainloopTimer.SetTimer(MainloopTimer_QueueSizedData, 200, 110, null); // TODO: maybe change it to be in its own thread?

            _iNetworkToken = broker.RegisterInterface<INetwork>(this);
            _iNetworkClientToken = broker.RegisterInterface<INetworkClient>(this);
            _iNetworkEncryptionToken = broker.RegisterInterface<INetworkEncryption>(this);
            return true;


            [ConfigHelp("Net", "InternalClientPort", ConfigScope.Global, typeof(int),
            Description = "The bind port for the internal client socket (used to communicate with biller and dirserver).")]
            bool InitializeSockets()
            {
                //
                // Listen sockets (pairs of game and ping sockets)
                //

                int x = 0;
                ListenData listenData;

                while ((listenData = CreateListenDataSockets(x++)) != null)
                {
                    _listenDataList.Add(listenData);

                    if (string.IsNullOrWhiteSpace(listenData.ConnectAs) == false)
                    {
                        if (!_pingData.ConnectAsPopulationStats.ContainsKey(listenData.ConnectAs))
                        {
                            _pingData.ConnectAsPopulationStats.Add(listenData.ConnectAs, new PopulationStats());
                        }
                    }

                    _logManager.LogM(LogLevel.Drivel, nameof(Network), $"Listening on {listenData.GameSocket.LocalEndPoint}.");
                }

                //
                // Client socket (for communicating with the biller and directory server)
                //

                int bindPort = _configManager.GetInt(_configManager.Global, "Net", "InternalClientPort", 0);

                try
                {
                    _clientSocket = CreateSocket(bindPort, IPAddress.Any);
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"Unable to create socket for client connections. {ex.Message}");
                }

                return true;

                [ConfigHelp("Listen", "Port", ConfigScope.Global, typeof(int),
                    "The port that the game protocol listens on. Sections named " +
                    "Listen1, Listen2, ... are also supported. All Listen " +
                    "sections must contain a port setting.")]
                [ConfigHelp("Listen", "BindAddress", ConfigScope.Global, typeof(string),
                    "The interface address to bind to. This is optional, and if " +
                    "omitted, the server will listen on all available interfaces.")]
                [ConfigHelp("Listen", "ConnectAs", ConfigScope.Global, typeof(string),
                    "This setting allows you to treat clients differently" +
                    "depending on which port they connect to. It serves as a" +
                    "virtual server identifier for the rest of the server.The" +
                    "standard arena placement module will use this as the name of" +
                    "a default arena to put clients who connect through this port" +
                    "in.")]
                [ConfigHelp("Listen", "AllowVIE", ConfigScope.Global, typeof(bool),
                    "Whether VIE protocol clients (i.e., Subspace 1.34 and bots) are allowed to connect to this port.")]
                [ConfigHelp("Listen", "AllowCont", ConfigScope.Global, typeof(bool),
                    "Whether Continuum clients are allowed to connect to this port.")]
                ListenData CreateListenDataSockets(int configIndex)
                {
                    string configSection = (configIndex == 0) ? "Listen" : $"Listen{configIndex}";

                    int gamePort = _configManager.GetInt(_configManager.Global, configSection, "Port", -1);
                    if (gamePort == -1)
                        return null;

                    string bindAddressStr = _configManager.GetStr(_configManager.Global, configSection, "BindAddress");
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
                        AllowVIE = _configManager.GetInt(_configManager.Global, configSection, "AllowVIE", 1) > 0,
                        AllowContinuum = _configManager.GetInt(_configManager.Global, configSection, "AllowCont", 1) > 0,
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
                        byte[] optionInValue = new byte[] { 0, 0, 0, 0 };
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

        bool IModuleLoaderAware.PostLoad(ComponentBroker broker)
        {
            // NOOP
            return true;
        }

        bool IModuleLoaderAware.PreUnload(ComponentBroker broker)
        {
            ReadOnlySpan<byte> disconnectSpan = stackalloc byte[] { 0x00, 0x07 };

            //
            // Disconnect all clients nicely
            //

            _playerData.Lock();

            try
            {
                foreach (Player player in _playerData.Players)
                {
                    if (IsOurs(player))
                    {
                        if (!player.TryGetExtraData(_connKey, out ConnData conn))
                            continue;

                        SendRaw(conn, disconnectSpan);

                        if (conn.enc != null)
                        {
                            conn.enc.Void(player);
                            broker.ReleaseInterface(ref conn.enc, conn.iEncryptName);
                        }
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            //
            // And disconnect our client connections
            //

            _clientConnectionsLock.EnterWriteLock();

            try
            {
                foreach (NetClientConnection cc in _clientConnections.Values)
                {
                    SendRaw(cc.ConnData, disconnectSpan);
                    cc.Handler.Disconnected();

                    if (cc.Encryptor != null)
                    {
                        cc.Encryptor.Void(cc);
                        broker.ReleaseInterface(ref cc.Encryptor, cc.EncryptorName);
                    }
                }

                _clientConnections.Clear();
            }
            finally
            {
                _clientConnectionsLock.ExitWriteLock();
            }

            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iNetworkToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iNetworkClientToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iNetworkEncryptionToken) != 0)
                return false;

            _mainloopTimer.ClearTimer(MainloopTimer_QueueSizedData, null);

            // stop threads
            _stopCancellationTokenSource.Cancel();

            for (int x = 0; x < _reliableThreads.Count; x++)
            {
                _relqueue.Enqueue(null);
            }

            foreach (Thread thread in _threadList)
            {
                thread.Join();
            }
            _threadList.Clear();
            _reliableThreads.Clear();

            _mainloop.WaitForMainWorkItemDrain();

            Array.Clear(_handlers, 0, _handlers.Length);
            Array.Clear(_nethandlers, 0, _nethandlers.Length); // for some reason ASSS doesn't clear this?
            Array.Clear(_sizedhandlers, 0, _sizedhandlers.Length);
            _relqueue.Clear();

            // close all sockets
            foreach (ListenData listenData in _listenDataList)
            {
                listenData.GameSocket.Close();
                listenData.PingSocket.Close();
            }

            _listenDataList.Clear();
            _pingData.ConnectAsPopulationStats.Clear();

            _clientSocket.Close();
            _clientSocket = null;

            _objectPoolManager.TryRemoveTracked(_bufferNodePool);

            _playerData.FreePlayerData(_connKey);

            return true;
        }

        #endregion

        #region INetworkEncryption Members

        void INetworkEncryption.AppendConnectionInitHandler(ConnectionInitHandler handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

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

        bool INetworkEncryption.RemoveConnectionInitHandler(ConnectionInitHandler handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

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

        void INetworkEncryption.ReallyRawSend(IPEndPoint remoteEndPoint, ReadOnlySpan<byte> data, ListenData ld)
        {
            if (remoteEndPoint == null)
                throw new ArgumentNullException(nameof(remoteEndPoint));

            if (data.Length < 1)
                throw new ArgumentOutOfRangeException(nameof(data), "There needs to be at least 1 byte to send.");

            if (ld == null)
                throw new ArgumentNullException(nameof(ld));

#if CFG_DUMP_RAW_PACKETS
            DumpPk($"SENDRAW: {data.Length} bytes", data);
#endif

            try
            {
                ld.GameSocket.SendTo(data, SocketFlags.None, remoteEndPoint);
            }
            catch (SocketException ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(Network), $"SocketException with error code {ex.ErrorCode} when sending to {remoteEndPoint} with game socket {ld.GameSocket.LocalEndPoint}. {ex}");
                return;
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(Network), $"Exception when sending to {remoteEndPoint} with game socket {ld.GameSocket.LocalEndPoint}. {ex}");
                return;
            }

            Interlocked.Add(ref _globalStats.bytesent, (ulong)data.Length);
            Interlocked.Increment(ref _globalStats.pktsent);
        }

        Player INetworkEncryption.NewConnection(ClientType clientType, IPEndPoint remoteEndpoint, string iEncryptName, ListenData ld)
        {
            if (ld == null)
                throw new ArgumentNullException(nameof(ld));

            // certain ports may have restrictions on client types
            if ((clientType == ClientType.VIE && !ld.AllowVIE)
                || (clientType == ClientType.Continuum && !ld.AllowContinuum))
            {
                return null;
            }

            // try to find a matching player for the endpoint
            if (remoteEndpoint != null && _clientDictionary.TryGetValue(remoteEndpoint, out Player player))
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
                    _logManager.LogP(LogLevel.Error, nameof(Network), player, $"NewConnection called for an established address.");
                    return null;
                }
            }

            player = _playerData.NewPlayer(clientType);

            if (!player.TryGetExtraData(_connKey, out ConnData conn))
            {
                _logManager.LogP(LogLevel.Error, nameof(Network), player, $"NewConnection created a new player, but ConnData not found.");
                return null;
            }

            IEncrypt enc = null;
            if (iEncryptName != null)
            {
                enc = _broker.GetInterface<IEncrypt>(iEncryptName);

                if (enc == null)
                {
                    _logManager.LogP(LogLevel.Error, nameof(Network), player, $"NewConnection called to use IEncrypt '{iEncryptName}', but not found.");
                    return null;
                }
            }

            conn.Initalize(enc, iEncryptName, _bandwithLimiterProvider.New());
            conn.p = player;

            // copy data from ListenData
            conn.whichSock = ld.GameSocket;
            player.ConnectAs = ld.ConnectAs;

            player.IPAddress = remoteEndpoint.Address;

            player.ClientName = clientType switch
            {
                ClientType.VIE => "<ss/vie client>",
                ClientType.Continuum => "<continuum>",
                _ => "<unknown game client>",
            };

            if (remoteEndpoint != null)
            {
                conn.RemoteEndpoint = remoteEndpoint;

                _clientDictionary[remoteEndpoint] = player;
            }

            _playerData.WriteLock();
            try
            {
                player.Status = PlayerState.Connected;
            }
            finally
            {
                _playerData.WriteUnlock();
            }

            if (remoteEndpoint != null)
            {
                _logManager.LogP(LogLevel.Drivel, nameof(Network), player, $"New connection from {remoteEndpoint}.");
            }
            else
            {
                _logManager.LogP(LogLevel.Drivel, nameof(Network), player, $"New internal connection.");
            }

            return player;
        }

        #endregion

        #region INetwork Members

        void INetwork.SendToOne(Player player, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            SendToOne(player, data, flags);
        }

        void INetwork.SendToOne<TData>(Player player, ref TData data, NetSendFlags flags) where TData : struct
        {
            ((INetwork)this).SendToOne(player, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1)), flags);
        }

        void INetwork.SendToArena(Arena arena, Player except, ReadOnlySpan<byte> data, NetSendFlags flags)
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
                            && (p.Arena == arena || arena == null)
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

        void INetwork.SendToArena<TData>(Arena arena, Player except, ref TData data, NetSendFlags flags) where TData : struct
        {
            ((INetwork)this).SendToArena(arena, except, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1)), flags);
        }

        void INetwork.SendToSet(HashSet<Player> set, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            SendToSet(set, data, flags);
        }

        void INetwork.SendToSet<TData>(HashSet<Player> set, ref TData data, NetSendFlags flags) where TData : struct
        {
            ((INetwork)this).SendToSet(set, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1)), flags);
        }

        void INetwork.SendWithCallback(Player player, ReadOnlySpan<byte> data, ReliableDelegate callback)
        {
            ReliableCallbackInvoker invoker = _reliableCallbackInvokerPool.Get();
            invoker.SetCallback(callback);
            SendWithCallback(player, data, invoker);
        }

        void INetwork.SendWithCallback<TData>(Player player, ref TData data, ReliableDelegate callback)
        {
            ((INetwork)this).SendWithCallback(player, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1)), callback);
        }

        void INetwork.SendWithCallback<TState>(Player player, ReadOnlySpan<byte> data, ReliableDelegate<TState> callback, TState clos)
        {
            ReliableCallbackInvoker<TState> invoker = _objectPoolManager.GetPool<ReliableCallbackInvoker<TState>>().Get();
            invoker.SetCallback(callback, clos);
            SendWithCallback(player, data, invoker);
        }

        void INetwork.SendWithCallback<TData, TState>(Player player, ref TData data, ReliableDelegate<TState> callback, TState clos)
        {
            ((INetwork)this).SendWithCallback(player, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1)), callback, clos);
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

        void INetwork.SendToTarget<TData>(ITarget target, ref TData data, NetSendFlags flags)
        {
            ((INetwork)this).SendToTarget(target, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1)), flags);
        }

        bool INetwork.SendSized<T>(Player player, int len, GetSizedSendDataDelegate<T> requestCallback, T clos)
        {
            if (player == null)
            {
                return false;
            }

            if (len <= 0)
            {
                return false;
            }

            if (!IsOurs(player))
            {
                _logManager.LogP(LogLevel.Drivel, nameof(Network), player, "Tried to send sized data to non-udp client.");
                return false;
            }

            if (!player.TryGetExtraData(_connKey, out ConnData conn))
                return false;

            SizedSendData<T> sd = new(requestCallback, clos, len, 0);

            lock (conn.olmtx)
            {
                conn.sizedsends.AddLast(sd);
            }

            return true;
        }

        void INetwork.AddPacket(C2SPacketType packetType, PacketDelegate func)
        {
            if (func is null)
                return;

            int packetTypeInt = (int)packetType;
            if (packetTypeInt >= 0 && packetTypeInt < MAXTYPES)
            {
                PacketDelegate d = _handlers[packetTypeInt];
                _handlers[packetTypeInt] = (d == null) ? func : (d += func);
            }
            else if ((packetTypeInt & 0xFF) == 0)
            {
                int b2 = packetTypeInt >> 8;

                if (b2 >= 0 && b2 < _nethandlers.Length && _nethandlers[b2] == null)
                {
                    _nethandlers[b2] = func;
                }
            }
        }

        void INetwork.RemovePacket(C2SPacketType packetType, PacketDelegate func)
        {
            if (func is null)
                return;

            int packetTypeInt = (int)packetType;
            if (packetTypeInt >= 0 && packetTypeInt < MAXTYPES)
            {
                PacketDelegate d = _handlers[packetTypeInt];
                if (d != null)
                {
                    _handlers[packetTypeInt] = (d -= func);
                }
            }
            else if ((packetTypeInt & 0xFF) == 0)
            {
                int b2 = packetTypeInt >> 8;

                if (b2 >= 0 && b2 < _nethandlers.Length && _nethandlers[b2] == func)
                {
                    _nethandlers[b2] = null;
                }
            }
        }

        void INetwork.AddSizedPacket(C2SPacketType packetType, SizedPacketDelegate func)
        {
            if (func is null)
                return;

            int packetTypeInt = (int)packetType;
            if (packetTypeInt >= 0 && packetTypeInt < MAXTYPES)
            {
                SizedPacketDelegate d = _sizedhandlers[packetTypeInt];
                _sizedhandlers[packetTypeInt] = (d == null) ? func : (d += func);
            }
        }

        void INetwork.RemoveSizedPacket(C2SPacketType packetType, SizedPacketDelegate func)
        {
            if (func is null)
                return;

            int packetTypeInt = (int)packetType;
            if (packetTypeInt >= 0 && packetTypeInt < MAXTYPES)
            {
                SizedPacketDelegate d = _sizedhandlers[packetTypeInt];
                if (d != null)
                {
                    _sizedhandlers[packetTypeInt] = (d -= func);
                }
            }
        }

        IReadOnlyNetStats INetwork.GetStats()
        {
            ulong objectsCreated = Convert.ToUInt64(_bufferPool.ObjectsCreated);
            Interlocked.Exchange(ref _globalStats.buffercount, objectsCreated);
            Interlocked.Exchange(ref _globalStats.buffersused, objectsCreated - Convert.ToUInt64(_bufferPool.ObjectsAvailable));

            return _globalStats;
        }

        void INetwork.GetClientStats(Player player, ref NetClientStats stats)
        {
            if (!player.TryGetExtraData(_connKey, out ConnData conn))
                return;

            stats.s2cn = conn.s2cn;
            stats.c2sn = conn.c2sn;
            stats.PacketsSent = conn.pktSent;
            stats.PacketsReceived = conn.pktReceived;
            stats.BytesSent = conn.bytesSent;
            stats.BytesReceived = conn.bytesReceived;
            stats.PacketsDropped = conn.pktdropped;

            if (conn.enc != null)
            {
                stats.EncryptionName = conn.iEncryptName;
            }

            stats.IPEndPoint = conn.RemoteEndpoint;

            conn.BandwidthLimiter.GetInfo(stats.BandwidthLimitInfo);
        }

        TimeSpan INetwork.GetLastPacketTimeSpan(Player player)
        {
            if (!player.TryGetExtraData(_connKey, out ConnData conn))
                return TimeSpan.Zero;

            //lock (conn.) // TODO: need to figure out locking
            {
                return DateTime.UtcNow - conn.lastPkt;
            }
        }

        bool INetwork.TryGetListenData(int index, out IPEndPoint endPoint, out string connectAs)
        {
            if (index >= _listenDataList.Count)
            {
                endPoint = default;
                connectAs = default;
                return false;
            }

            ListenData ld = _listenDataList[index];
            endPoint = ld.GameSocket.LocalEndPoint as IPEndPoint;

            if (endPoint == null)
            {
                connectAs = default;
                return false;
            }

            connectAs = ld.ConnectAs;
            return true;
        }

        bool INetwork.TryGetPopulationStats(string connectAs, out uint total, out uint playing)
        {
            if (!_pingData.ConnectAsPopulationStats.TryGetValue(connectAs, out var stats))
            {
                total = default;
                playing = default;
                return false;
            }

            total = stats.Total;
            playing = stats.Playing;
            return true;
        }

        IReadOnlyList<ListenData> INetwork.Listening => _readOnlyListenData;

        #endregion

        #region INetworkClient Members

        ClientConnection INetworkClient.MakeClientConnection(string address, int port, IClientConnectionHandler handler, string iClientEncryptName)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("Cannot be null or white-space.", nameof(address));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));

            if (!IPAddress.TryParse(address, out IPAddress ipAddress))
                throw new ArgumentException("Unable to parse as an IP address.", nameof(address));

            IPEndPoint remoteEndpoint = new(ipAddress, port);

            IClientEncrypt encryptor = null;
            if (!string.IsNullOrWhiteSpace(iClientEncryptName))
            {
                encryptor = _broker.GetInterface<IClientEncrypt>(iClientEncryptName);
                if (encryptor == null)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"Unable to find an {nameof(IClientEncrypt)} named {iClientEncryptName}.");
                    return null;
                }
            }

            NetClientConnection cc = new(remoteEndpoint, _clientSocket, handler, encryptor, iClientEncryptName, _bandwithLimiterProvider.New());

            encryptor?.Initialze(cc);

            bool added;

            _clientConnectionsLock.EnterWriteLock();

            try
            {
                added = _clientConnections.TryAdd(remoteEndpoint, cc);
            }
            finally
            {
                _clientConnectionsLock.ExitWriteLock();
            }

            if (!added)
            {
                _logManager.LogM(LogLevel.Error, nameof(Network), $"Attempt to make a client connection to {remoteEndpoint} when one already exists.");
                return null;
            }

            ConnectionInitPacket packet = new((int)(_prng.Get32() | 0x80000000), 1);
            SendRaw(cc.ConnData, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref packet, 1)));

            return cc;
        }

        void INetworkClient.SendPacket(ClientConnection cc, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            if (cc == null)
                return;

            if (cc is not NetClientConnection ncc)
                throw new ArgumentException("Unsupported client connection. It must be created by this module.", nameof(cc));

            SendToOne(ncc.ConnData, data, flags);
        }

        void INetworkClient.SendPacket<T>(ClientConnection cc, ref T data, NetSendFlags flags)
        {
            ((INetworkClient)this).SendPacket(cc, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1)), flags);
        }

        void INetworkClient.DropConnection(ClientConnection cc)
        {
            if (cc is not NetClientConnection ncc)
                throw new ArgumentException("Unsupported client connection. It must be created by this module.", nameof(cc));

            DropConnection(ncc);
        }

        #endregion

        #region Core packet handlers (oohandlers)

        private void CorePacket_KeyResponse(ReadOnlySpan<byte> data, ConnData conn)
        {
            if (conn is null)
                return;

            if (data.Length != 6)
                return;

            if (conn.cc is not null)
                conn.cc.Handler.Connected();
            else if (conn.p is not null)
                _logManager.LogP(LogLevel.Malicious, nameof(Network), conn.p, "Got key response packet.");
        }

        private void CorePacket_Reliable(ReadOnlySpan<byte> data, ConnData conn)
        {
            if (conn is null)
                return;

            if (data.Length < 7) // at least enough for the header + 1 byte of data
            {
                return;
            }

            ref readonly ReliableHeader rp = ref MemoryMarshal.AsRef<ReliableHeader>(data);
            int sn = rp.SeqNum;

            Monitor.Enter(conn.relmtx);

            if ((sn - conn.c2sn) >= Constants.CFG_INCOMING_BUFFER || sn < 0)
            {
                Monitor.Exit(conn.relmtx);

                // just drop it
                if (conn.p is not null)
                    _logManager.LogM(LogLevel.Drivel, nameof(Network), $"[{conn.p.Name}] [pid={conn.p.Id}] Reliable packet with too big delta ({sn} - {conn.c2sn}).");
                else
                    _logManager.LogM(LogLevel.Drivel, nameof(Network), $"(client connection) Reliable packet with too big delta ({sn} - {conn.c2sn}).");
            }
            else
            {
                // store it and ack
                bool canProcess = false;

                // add to rel stuff to be processed
                int spot = sn % Constants.CFG_INCOMING_BUFFER;
                if ((sn < conn.c2sn) || (conn.relbuf[spot] != null))
                {
                    // a dup
                    conn.relDups++;
                }
                else
                {
                    SubspaceBuffer buffer = _bufferPool.Get();
                    buffer.Conn = conn;
                    data.CopyTo(buffer.Bytes);
                    buffer.NumBytes = data.Length;

                    conn.relbuf[spot] = buffer;
                    canProcess = (sn == conn.c2sn);
                }

                Monitor.Exit(conn.relmtx);

                // send the ack
                AckPacket ap = new(sn);

                lock (conn.olmtx)
                {
                    SendOrBufferPacket(
                        conn,
                        MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref ap, 1)),
                        NetSendFlags.Ack);
                }

                if (canProcess)
                {
                    // add to global rel list for processing
                    _relqueue.Enqueue(conn);
                }
            }
        }

        private void CorePacket_Ack(ReadOnlySpan<byte> data, ConnData conn)
        {
            if (conn is null)
                return;

            if (data.Length != 6) // ack packets are 6 bytes long
                return;

            ref readonly AckPacket ack = ref MemoryMarshal.AsRef<AckPacket>(data);
            int seqNum = ack.SeqNum;

            Monitor.Enter(conn.olmtx);

            LinkedList<SubspaceBuffer> outlist = conn.outlist[(int)BandwidthPriority.Reliable];
            LinkedListNode<SubspaceBuffer> nextNode = null;
            for (LinkedListNode<SubspaceBuffer> node = outlist.First; node != null; node = nextNode)
            {
                nextNode = node.Next;

                SubspaceBuffer b = node.Value;
                ref ReliableHeader brp = ref MemoryMarshal.AsRef<ReliableHeader>(b.Bytes);
                if (seqNum == brp.SeqNum)
                {
                    outlist.Remove(node);
                    _bufferNodePool.Return(node);
                    Monitor.Exit(conn.olmtx);

                    if (b.CallbackInvoker != null)
                    {
                        QueueReliableCallback(b.CallbackInvoker, conn.p, true);
                        b.CallbackInvoker = null; // the workitem is now responsible for disposing the callback invoker
                    }

                    if (b.Tries == 1)
                    {
                        int rtt = (int)DateTime.UtcNow.Subtract(b.LastRetry).TotalMilliseconds;
                        if (rtt < 0)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(Network), $"Negative rtt ({rtt}); clock going backwards.");
                            rtt = 100;
                        }

                        int dev = conn.avgrtt - rtt;
                        if (dev < 0)
                            dev = -dev;

                        conn.rttdev = (conn.rttdev * 3 + dev) / 4;
                        conn.avgrtt = (conn.avgrtt * 7 + rtt) / 8;

                        if (_lagCollect != null && conn.p != null)
                            _lagCollect.RelDelay(conn.p, rtt);
                    }

                    b.Dispose();

                    // handle limit adjustment
                    conn.BandwidthLimiter.AdjustForAck();

                    return;
                }
            }

            Monitor.Exit(conn.olmtx);
        }

        private void CorePacket_SyncRequest(ReadOnlySpan<byte> data, ConnData conn)
        {
            if (conn is null)
                return;

            if (data.Length != TimeSyncRequest.Length)
                return;

            ref readonly TimeSyncRequest cts = ref MemoryMarshal.AsRef<TimeSyncRequest>(data);
            uint clientTime = cts.Time;
            uint serverTime = ServerTick.Now;

            TimeSyncResponse ts = new();
            ts.Initialize(clientTime, serverTime);

            lock (conn.olmtx)
            {
                // note: this bypasses bandwidth limits
                SendRaw(conn, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref ts, 1)));

                // submit data to lagdata
                if (_lagCollect is not null && conn.p is not null)
                {
                    TimeSyncData timeSyncData = new()
                    {
                        ServerPacketsReceived = conn.pktReceived,
                        ServerPacketsSent = conn.pktSent,
                        ClientPacketsReceived = cts.PktRecvd,
                        ClientPacketsSent = cts.PktSent,
                        ServerTime = serverTime,
                        ClientTime = clientTime,
                    };

                    _lagCollect.TimeSync(conn.p, in timeSyncData);
                }
            }
        }

        private void CorePacket_Drop(ReadOnlySpan<byte> data, ConnData conn)
        {
            if (conn is null)
                return;

            if (data.Length != 2)
                return;

            if (conn.p is not null)
            {
                _playerData.KickPlayer(conn.p);
            }
            else if (conn.cc is not null)
            {
                conn.cc.Handler.Disconnected();
                // TODO: This sends an extra 00 07 to the client that should probably go away. 
                DropConnection(conn.cc);
            }
        }

        private void CorePacket_BigData(ReadOnlySpan<byte> data, ConnData conn)
        {
            if (conn is null)
                return;

            if (data.Length < 3) // 0x00, [0x08 or 0x09], and then at least one byte of data
                return;

            lock (conn.bigmtx)
            {
                // Get a BigReceive object if we don't already have one and give it to the ConnData object to 'own'.
                conn.BigRecv ??= _bigReceivePool.Get();

                int newSize = conn.BigRecv.Size + data.Length - 2;

                if (newSize <= 0 || newSize > Constants.MaxBigPacket)
                {
                    if (conn.p is not null)
                        _logManager.LogP(LogLevel.Malicious, nameof(Network), conn.p, $"Refusing to allocate {newSize} bytes (> {Constants.MaxBigPacket}).");
                    else if (conn.cc is not null)
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), $"(client connection) Refusing to allocate {newSize} bytes (> {Constants.MaxBigPacket}).");

                    conn.BigRecv.Dispose();
                    conn.BigRecv = null;
                    return;
                }

                // Append the data.
                conn.BigRecv.Append(data[2..]); // data only, header removed

                if (data[1] == 0x08)
                    return;

                // Getting here means the we got 0x09 (end of "Big" data packet stream), so we should process it now.
                if (conn.BigRecv.Buffer[0] > 0 && conn.BigRecv.Buffer[0] < MAXTYPES)
                {
                    // Take ownership of the BigReceive object from the ConnData object.
                    BigReceive bigReceive = conn.BigRecv;
                    conn.BigRecv = null;

                    // Process it on the mainloop thread.
                    // Ownership of the BigReceive object is transferred to the workitem. The workitem is responsible for disposing it.
                    _mainloop.QueueMainWorkItem(
                        MainloopWork_CallBigPacketHandlers,
                        new BigPacketWork()
                        {
                            ConnData = conn,
                            BigReceive = bigReceive,
                        });
                }
                else
                {
                    if (conn.p is not null)
                        _logManager.LogP(LogLevel.Warn, nameof(Network), conn.p, $"Bad type for bigpacket: {conn.BigRecv.Buffer[0]}.");
                    else if (conn.cc is not null)
                        _logManager.LogM(LogLevel.Warn, nameof(Network), $"(client connection) Bad type for bigpacket: {conn.BigRecv.Buffer[0]}.");

                    conn.BigRecv.Dispose();
                    conn.BigRecv = null;
                }
            }

            void MainloopWork_CallBigPacketHandlers(BigPacketWork work)
            {
                try
                {
                    if (work.ConnData is null || work.BigReceive is null || work.BigReceive.Buffer is null || work.BigReceive.Size < 1)
                        return;

                    CallPacketHandlers(work.ConnData, work.BigReceive.Buffer, work.BigReceive.Size);
                }
                finally
                {
                    // return the buffer to its pool
                    work.BigReceive?.Dispose();
                }
            }
        }

        private void CorePacket_SizedData(ReadOnlySpan<byte> data, ConnData conn)
        {
            if (conn is null)
                return;

            if (data.Length < 7)
                return;

            ref readonly PresizedHeader header = ref MemoryMarshal.AsRef<PresizedHeader>(data);
            int size = header.Size;
            data = data[PresizedHeader.Length..];

            lock (conn.bigmtx)
            {
                // only handle presized packets for player connections, not client connections
                if (conn.p is null)
                    return;

                if (conn.sizedrecv.offset == 0)
                {
                    // first packet
                    int type = data[0];
                    if (type < MAXTYPES)
                    {
                        conn.sizedrecv.type = type;
                        conn.sizedrecv.totallen = size;
                    }
                    else
                    {
                        EndSized(conn.p, false);
                    }
                }

                if (conn.sizedrecv.totallen != size)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(Network), conn.p, "Length mismatch in sized packet.");
                    EndSized(conn.p, false);
                }
                else if ((conn.sizedrecv.offset + data.Length - 6) > size)
                {
                    _logManager.LogP(LogLevel.Malicious, nameof(Network), conn.p, "Sized packet overflow.");
                    EndSized(conn.p, false);
                }
                else
                {
                    _sizedhandlers[conn.sizedrecv.type]?.Invoke(conn.p, data, conn.sizedrecv.offset, size);

                    conn.sizedrecv.offset += data.Length;

                    if (conn.sizedrecv.offset >= size)
                        EndSized(conn.p, true); // sized receive is complete
                }
            }
        }

        private void CorePacket_CancelSized(ReadOnlySpan<byte> data, ConnData conn)
        {
            if (conn is null)
                return;

            if (data.Length != 2)
                return;

            // the client has requested a cancel for the sized transfer
            lock (conn.olmtx)
            {
                // cancel current sized transfer
                LinkedListNode<ISizedSendData> node = conn.sizedsends.First;
                if (node is not null)
                {
                    ISizedSendData sd = node.Value;
                    sd?.RequestData(0, Span<byte>.Empty); // notify transfer complete
                    conn.sizedsends.RemoveFirst();
                }

                ReadOnlySpan<byte> cancelPresizedAckSpan = stackalloc byte[2] { 0x00, 0x0C };
                SendOrBufferPacket(conn, cancelPresizedAckSpan, NetSendFlags.Reliable);
            }
        }

        private void CorePacket_SizedCancelled(ReadOnlySpan<byte> data, ConnData conn)
        {
            if (conn is null)
                return;

            if (data.Length != 2)
                return;

            if (conn.p is not null)
            {
                lock (conn.bigmtx)
                {
                    EndSized(conn.p, false);
                }
            }
        }

        private void CorePacket_Grouped(ReadOnlySpan<byte> data, ConnData conn)
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

                ProcessBuffer(data.Slice(1, len), conn);

                data = data[(1 + len)..];
            }
        }

        private void CorePacket_Special(ReadOnlySpan<byte> data, ConnData conn)
        {
            if (conn is null)
                return;

            if (data.Length < 2)
                return;

            Player player = conn.p;
            if (player is null)
                return;

            int t2 = data[1];

            if (t2 < _nethandlers.Length)
            {
                // TODO: change the nethandlers to take Span<byte>, then we won't need to do the extra copy.
                using SubspaceBuffer buffer = _bufferPool.Get();
                data.CopyTo(buffer.Bytes);
                buffer.NumBytes = data.Length;
                _nethandlers[t2]?.Invoke(player, buffer.Bytes, buffer.NumBytes);
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _stopCancellationTokenSource.Dispose();
            _clientConnectionsLock.Dispose();
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
                if (ld.GameSocket != null)
                {
                    socketList.Add(ld.GameSocket);
                    endpointLookup.Add(ld.GameSocket.LocalEndPoint, ('G', ld));
                }

                if (ld.PingSocket != null)
                {
                    socketList.Add(ld.PingSocket);
                    endpointLookup.Add(ld.PingSocket.LocalEndPoint, ('P', ld));
                }
            }

            if (_clientSocket != null)
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
                        if (endpointLookup.TryGetValue(socket.LocalEndPoint, out var tuple))
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

                Span<byte> data;
                int bytesReceived;
                Player player;
                ConnData conn;
                IPEndPoint remoteIPEP = _objectPoolManager.IPEndPointPool.Get();

                try
                {
                    EndPoint remoteEP = remoteIPEP;

                    try
                    {
                        bytesReceived = ld.GameSocket.ReceiveFrom(_receiveBuffer, _receiveBuffer.Length, SocketFlags.None, ref remoteEP);
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

                    data = _receiveBuffer.AsSpan(0, bytesReceived);
                    bool isConnectionInitPacket = IsConnectionInitPacket(data);

                    // TODO: Add some type of denial of service / flood detection for bad packet sizes? block by ip/port?
                    // TODO: Add some type of denial of service / flood detection for repeated connection init attempts over a threshold? block by ip/port?

                    // TODO: Distinguish between actual connection init packets and peer packets by checking if the remote endpoint (IP + port) is from a configured peer.
                    // Connection init packets should be normal sized game packets, <= Constants.MaxPacket.
                    // Peer packets can be much larger than game packets.

                    if (isConnectionInitPacket && bytesReceived > Constants.MaxConnInitPacket)
                    {
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), $"Received a connection init packet that is too large ({bytesReceived} bytes) from {remoteEP}.");
                        return;
                    }
                    else if (!isConnectionInitPacket && bytesReceived > Constants.MaxPacket) // TODO: verify that this is the true maximum, I've read articles that said it was 520 (due to the VIE encryption limit)
                    {
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), $"Received a game packet that is too large ({bytesReceived} bytes) from {remoteEP}.");
                        return;
                    }

#if CFG_DUMP_RAW_PACKETS
                    DumpPk($"RECV: {bytesReceived} bytes", data);
#endif

                    if (remoteEP is not IPEndPoint remoteEndPoint)
                    {
                        return;
                    }

                    if (_clientDictionary.TryGetValue(remoteEndPoint, out player) == false)
                    {
                        // this might be a new connection. make sure it's really a connection init packet
                        if (isConnectionInitPacket)
                        {
                            ProcessConnectionInit(remoteEndPoint, _receiveBuffer, bytesReceived, ld);
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

                    if (!player.TryGetExtraData(_connKey, out conn))
                    {
                        return;
                    }

                    if (isConnectionInitPacket)
                    {
                        // here, we have a connection init, but it's from a
                        // player we've seen before. there are a few scenarios:
                        if (player.Status == PlayerState.Connected)
                        {
                            // if the player is in PlayerState.Connected, it means that
                            // the connection init response got dropped on the
                            // way to the client. we have to resend it.
                            ProcessConnectionInit(remoteEndPoint, _receiveBuffer, bytesReceived, ld);
                        }
                        else
                        {
                            // otherwise, he probably just lagged off or his
                            // client crashed. ideally, we'd postpone this
                            // packet, initiate a logout procedure, and then
                            // process it. we can't do that right now, so drop
                            // the packet, initiate the logout, and hope that
                            // the client re-sends it soon. 
                            _playerData.KickPlayer(player);
                        }

                        return;
                    }
                }
                finally
                {
                    _objectPoolManager.IPEndPointPool.Return(remoteIPEP);
                }

                // we shouldn't get packets in this state, but it's harmless if we do
                if (player.Status >= PlayerState.LeavingZone || player.WhenLoggedIn >= PlayerState.LeavingZone)
                {
                    return;
                }

                if (player.Status > PlayerState.TimeWait)
                {
                    _logManager.LogM(LogLevel.Warn, nameof(Network), $"[pid={player.Id}] Packet received from bad state {player.Status}.");

                    // don't set lastpkt time here
                    return;
                }

                conn.lastPkt = DateTime.UtcNow;
                conn.bytesReceived += (ulong)bytesReceived;
                conn.pktReceived++;
                Interlocked.Add(ref _globalStats.byterecvd, (ulong)bytesReceived);
                Interlocked.Increment(ref _globalStats.pktrecvd);

                IEncrypt enc = conn.enc;
                if (enc is not null)
                {
                    bytesReceived = enc.Decrypt(player, _receiveBuffer, bytesReceived);
                    data = _receiveBuffer.AsSpan(0, bytesReceived);
                }

                if (bytesReceived == 0)
                {
                    // bad crc, or something
                    _logManager.LogM(LogLevel.Malicious, nameof(Network), $"[pid={player.Id}] Failure decrypting packet.");
                    return;
                }

#if CFG_DUMP_RAW_PACKETS
                DumpPk($"RECV: about to process {bytesReceived} bytes", data);
#endif

                ProcessBuffer(data, conn);

                static bool IsConnectionInitPacket(ReadOnlySpan<byte> data)
                {
                    return data.Length >= 2
                        && data[0] == 0x00
                        && ((data[1] == 0x01) || (data[1] == 0x11));
                }

                bool ProcessConnectionInit(IPEndPoint remoteEndpoint, byte[] buffer, int len, ListenData ld)
                {
                    _connectionInitLock.EnterReadLock();

                    try
                    {
                        foreach (ConnectionInitHandler handler in _connectionInitHandlers)
                        {
                            if (handler(remoteEndpoint, buffer, len, ld))
                                return true;
                        }
                    }
                    finally
                    {
                        _connectionInitLock.ExitReadLock();
                    }

                    _logManager.LogM(LogLevel.Info, nameof(Network), $"Got a connection init packet, but no handler processed it.  Please verify that an encryption module is loaded or the {nameof(EncryptionNull)} module if no encryption is desired.");
                    return false;
                }
            }

            void HandlePingPacketReceived(ListenData ld)
            {
                if (ld is null)
                    return;

                IPEndPoint remoteIPEP = _objectPoolManager.IPEndPointPool.Get();

                try
                {
                    int numBytes;
                    EndPoint receivedFrom = remoteIPEP;

                    try
                    {
                        numBytes = ld.PingSocket.ReceiveFrom(_receiveBuffer, SocketFlags.None, ref receivedFrom);
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

                    if (receivedFrom is not IPEndPoint remoteEndPoint)
                        return;

                    if (numBytes <= 0)
                        return;

                    Span<byte> data = _receiveBuffer.AsSpan(0, 8);

                    //
                    // Refresh data (if needed)
                    //

                    if (_pingData.LastRefresh == null
                        || (DateTime.UtcNow - _pingData.LastRefresh) > _config.PingRefreshThreshold)
                    {
                        foreach (PopulationStats stats in _pingData.ConnectAsPopulationStats.Values)
                        {
                            stats.TempTotal = stats.TempPlaying = 0;
                        }

                        IArenaManager aman = _broker.GetInterface<IArenaManager>();
                        if (aman == null)
                            return;

                        try
                        {
                            aman.Lock();

                            try
                            {
                                aman.GetPopulationSummary(out int total, out int playing);

                                // global
                                _pingData.Global.Total = (uint)total;
                                _pingData.Global.Playing = (uint)playing;

                                // arenas that are associated with ListenData
                                foreach (Arena arena in aman.Arenas)
                                {
                                    if (_pingData.ConnectAsPopulationStats.TryGetValue(arena.BaseName, out PopulationStats stats))
                                    {
                                        stats.TempTotal += (uint)arena.Total;
                                        stats.TempPlaying += (uint)arena.Playing;
                                    }
                                }

                                _pingData.LastRefresh = DateTime.UtcNow;
                            }
                            finally
                            {
                                aman.Unlock();
                            }
                        }
                        finally
                        {
                            _broker.ReleaseInterface(ref aman);
                        }

                        foreach (PopulationStats stats in _pingData.ConnectAsPopulationStats.Values)
                        {
                            stats.Total = stats.TempTotal;
                            stats.Playing = stats.TempPlaying;
                        }

                        IPeer peer = _broker.GetInterface<IPeer>();
                        if (peer is not null)
                        {
                            try
                            {
                                _pingData.Global.Total += (uint)peer.GetPopulationSummary();
                                // peer protocol does not provide a "playing" count
                            }
                            finally
                            {
                                _broker.ReleaseInterface(ref peer);
                            }
                        }
                    }

                    //
                    // Respond
                    //

                    if (numBytes == 4)
                    {
                        // bytes from receive
                        data[4] = data[0];
                        data[5] = data[1];
                        data[6] = data[2];
                        data[7] = data[3];

                        // # of clients
                        // Note: ASSS documentation says it's a UInt32, but it appears Continuum looks at only the first 2 bytes as an UInt16.
                        Span<byte> countSpan = data[..4];

                        if (string.IsNullOrWhiteSpace(ld.ConnectAs))
                        {
                            // global
                            BinaryPrimitives.WriteUInt32LittleEndian(countSpan, _pingData.Global.Total);
                        }
                        else
                        {
                            // specific arena/zone
                            BinaryPrimitives.WriteUInt32LittleEndian(countSpan, _pingData.ConnectAsPopulationStats[ld.ConnectAs].Total);
                        }

                        try
                        {
                            int bytesSent = ld.PingSocket.SendTo(data, SocketFlags.None, remoteEndPoint);
                        }
                        catch (SocketException ex)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(Network), $"SocketException with error code {ex.ErrorCode} when sending to {remoteEndPoint} with ping socket {ld.PingSocket.LocalEndPoint}. {ex}");
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(Network), $"Exception when sending to {remoteEndPoint} with ping socket {ld.PingSocket.LocalEndPoint}. {ex}");
                            return;
                        }
                    }
                    else if (numBytes == 8)
                    {
                        // TODO: add the ability handle ASSS' extended ping packets
                    }
                }
                finally
                {
                    _objectPoolManager.IPEndPointPool.Return(remoteIPEP);
                }

                Interlocked.Increment(ref _globalStats.pcountpings);
            }

            void HandleClientPacketReceived()
            {
                int bytesReceived;
                IPEndPoint remoteIPEP = _objectPoolManager.IPEndPointPool.Get();

                try
                {
                    EndPoint receivedFrom = remoteIPEP;

                    try
                    {
                        bytesReceived = _clientSocket.ReceiveFrom(_receiveBuffer, _receiveBuffer.Length, SocketFlags.None, ref receivedFrom);
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
                    DumpPk($"RAW CLIENT DATA: {bytesReceived} bytes", data);
#endif

                    bool found;
                    NetClientConnection cc;

                    _clientConnectionsLock.EnterReadLock();

                    try
                    {
                        found = _clientConnections.TryGetValue(receivedFrom, out cc);
                    }
                    finally
                    {
                        _clientConnectionsLock.ExitReadLock();
                    }

                    if (found)
                    {
                        ConnData conn = cc.ConnData;

                        if (cc.Encryptor is not null)
                        {
                            bytesReceived = cc.Encryptor.Decrypt(cc, _receiveBuffer, bytesReceived);
                            data = _receiveBuffer.AsSpan(0, bytesReceived);

#if CFG_DUMP_RAW_PACKETS
                            DumpPk($"DECRYPTED CLIENT DATA: {bytesReceived} bytes", data);
#endif
                        }

                        if (bytesReceived > 0)
                        {
                            conn.lastPkt = DateTime.UtcNow;
                            conn.bytesReceived += (ulong)bytesReceived;
                            conn.pktReceived++;
                            Interlocked.Add(ref _globalStats.byterecvd, (ulong)bytesReceived);
                            Interlocked.Increment(ref _globalStats.pktrecvd);

                            ProcessBuffer(data, conn);
                        }
                        else
                        {
                            _logManager.LogM(LogLevel.Malicious, nameof(Network), "(client connection) Failed to decrypt packet.");
                        }

                        return;
                    }

                    _logManager.LogM(LogLevel.Warn, nameof(Network), $"Got data on the client port that was not from any known connection ({receivedFrom}).");
                }
                finally
                {
                    _objectPoolManager.IPEndPointPool.Return(remoteIPEP);
                }
            }
        }

        private void SendThread()
        {
            Span<byte> groupedPacketBuffer = stackalloc byte[Constants.MaxGroupedPacketLength];
            PacketGrouper packetGrouper = new(this, groupedPacketBuffer);

            List<Player> toKick = new();
            List<Player> toFree = new();
            List<NetClientConnection> toDrop = new();

            while (_stopToken.IsCancellationRequested == false)
            {
                // first send outgoing packets (players)
                _playerData.Lock();

                try
                {
                    foreach (Player p in _playerData.Players)
                    {
                        if (p.Status >= PlayerState.Connected
                            && p.Status < PlayerState.TimeWait
                            && IsOurs(p))
                        {
                            if (!p.TryGetExtraData(_connKey, out ConnData conn))
                                continue;

                            if (Monitor.TryEnter(conn.olmtx))
                            {
                                try
                                {
                                    SendOutgoing(conn, ref packetGrouper);
                                    SubmitRelStats(p);
                                }
                                finally
                                {
                                    Monitor.Exit(conn.olmtx);
                                }
                            }

                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                // process lagouts and timewait
                _playerData.Lock();
                DateTime now = DateTime.UtcNow;
                try
                {
                    foreach (Player p in _playerData.Players)
                    {
                        if (p.Status >= PlayerState.Connected
                            && IsOurs(p))
                        {
                            ProcessLagouts(p, now, toKick, toFree);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                // now kick the ones we needed to above
                if (toKick.Count > 0)
                {
                    foreach (Player p in toKick)
                    {
                        _playerData.KickPlayer(p);
                    }

                    toKick.Clear();
                }

                // and free ...
                if (toFree.Count > 0)
                {
                    foreach (Player p in toFree)
                    {
                        if (!p.TryGetExtraData(_connKey, out ConnData conn))
                            continue;

                        // one more time, just to be sure
                        ClearBuffers(p);

                        _bandwithLimiterProvider.Free(conn.BandwidthLimiter);
                        conn.BandwidthLimiter = null;

                        _playerData.FreePlayer(p);
                    }

                    toFree.Clear();
                }

                // outgoing packets and lagouts for client connections
                now = DateTime.UtcNow;

                _clientConnectionsLock.EnterUpgradeableReadLock();

                try
                {
                    foreach (NetClientConnection cc in _clientConnections.Values)
                    {
                        ConnData conn = cc.ConnData;
                        lock (conn.olmtx)
                        {
                            SendOutgoing(conn, ref packetGrouper);
                        }

                        if (conn.HitMaxRetries)
                        {
                            _logManager.LogM(LogLevel.Warn, nameof(Network), "Client connection hit max retries.");
                            toDrop.Add(cc);
                        }
                        else
                        {
                            // Check whether it's been too long since we received a packet.
                            // Use a limit of 10 seconds for new connections; otherwise a limit of 65 seconds.
                            TimeSpan limit = conn.pktReceived > 0 ? TimeSpan.FromSeconds(65) : TimeSpan.FromSeconds(10);
                            TimeSpan actual = now - conn.lastPkt;

                            if (actual > limit)
                            {
                                _logManager.LogM(LogLevel.Warn, nameof(Network), $"Client connection hit no-data time limit {actual} > {limit}.");
                                toDrop.Add(cc);
                            }
                        }
                    }

                    // drop any connections that need to be dropped
                    if (toDrop.Count > 0)
                    {
                        foreach (NetClientConnection cc in toDrop)
                        {
                            cc.Handler.Disconnected();
                            DropConnection(cc);
                        }

                        toDrop.Clear();
                    }
                }
                finally
                {
                    _clientConnectionsLock.ExitUpgradeableReadLock();
                }

                if (_stopToken.IsCancellationRequested)
                    return;

                // TODO: Figure out a way that's better than a plain Sleep.
                // Maybe separate out lagout/kick/free logic to a threadpool timer? leaving only outgoing/stats logic?
                // Then maybe an AutoResetEvent for whenever a new outgoing packet is queued?
                // and wait on the AutoResetEvent/stopToken.WaitHandle? 
                // How to tell how long until a player's bandwidth limits pass a threshold that allows sending more?
                // Need to investigate bandwidth limiting logic before deciding on this.
                Thread.Sleep(10); // 1/100 second
            }

            void SendOutgoing(ConnData conn, ref PacketGrouper packetGrouper)
            {
                DateTime now = DateTime.UtcNow;

                // use an estimate of the average round-trip time to figure out when to resend a packet
                uint timeout = Math.Clamp((uint)(conn.avgrtt + (4 * conn.rttdev)), 250, 2000);

                // update the bandwidth limiter's counters
                conn.BandwidthLimiter.Iter(now);

                int canSend = conn.BandwidthLimiter.GetCanBufferPackets();
                int retries = 0;
                int outlistlen = 0;

                packetGrouper.Initialize();

                // process the highest priority first
                for (int pri = conn.outlist.Length - 1; pri >= 0; pri--)
                {
                    LinkedList<SubspaceBuffer> outlist = conn.outlist[pri];

                    if (pri == (int)BandwidthPriority.Reliable)
                    {
                        // move packets from UnsentRelOutList to outlist, grouped if possible
                        while (conn.UnsentRelOutList.Count > 0)
                        {
                            if (outlist.Count > 0)
                            {
                                // The reliable sending/pending queue has at least one packet,
                                // Get the first one (lowest sequence number) and use that number to determine if there's room to add more.
                                ref ReliableHeader min = ref MemoryMarshal.AsRef<ReliableHeader>(outlist.First.Value.Bytes);
                                if ((conn.s2cn - min.SeqNum) > canSend)
                                {
                                    break;
                                }
                            }

                            LinkedListNode<SubspaceBuffer> n1 = conn.UnsentRelOutList.First;
                            SubspaceBuffer b1 = conn.UnsentRelOutList.First.Value;
#if !DISABLE_GROUPED_SEND
                            if (b1.NumBytes <= Constants.MaxGroupedPacketItemLength && conn.UnsentRelOutList.Count > 1)
                            {
                                // The 1st packet can fit into a grouped packet and there's at least one more packet available, check if it's possible to group them together
                                SubspaceBuffer b2 = conn.UnsentRelOutList.First.Next.Value;

                                // Note: At the moment I think it is still more beneficial to group as many as possible even if it does go over 255 bytes. However, I've made it configurable.
                                int maxRelGroupedPacketLength = _config.LimitReliableGroupingSize
                                    ? Constants.MaxGroupedPacketItemLength // limit reliable packet grouping up to 255 bytes so that the result can still fit into another grouped packet later on
                                    : Constants.MaxGroupedPacketLength; // (default) group as many as is possible to fit into a fully sized grouped packet

                                if (b2.NumBytes <= Constants.MaxGroupedPacketItemLength // the 2nd packet can fit into a grouped packet too
                                    && (ReliableHeader.Length + 2 + 1 + b1.NumBytes + 1 + b2.NumBytes) <= maxRelGroupedPacketLength) // can fit together in a reliable packet containing a grouped packet containing both packets
                                {
                                    // We know we can group at least the first 2
                                    SubspaceBuffer groupedBuffer = _bufferPool.Get();
                                    groupedBuffer.Conn = conn;
                                    groupedBuffer.Flags = b1.Flags; // taking the flags from the first packet, though doesn't really matter since we already know it's reliable
                                    groupedBuffer.LastRetry = DateTime.UtcNow.Subtract(new TimeSpan(0, 0, 10));
                                    groupedBuffer.Tries = 0;

                                    ref ReliableHeader groupedRelHeader = ref MemoryMarshal.AsRef<ReliableHeader>(groupedBuffer.Bytes);
                                    groupedRelHeader.Initialize(conn.s2cn++);

                                    // Group up as many as possible
                                    PacketGrouper relGrouper = new(this, groupedBuffer.Bytes.AsSpan(ReliableHeader.Length, maxRelGroupedPacketLength - ReliableHeader.Length));
                                    LinkedListNode<SubspaceBuffer> node = conn.UnsentRelOutList.First;
                                    while (node != null)
                                    {
                                        LinkedListNode<SubspaceBuffer> next = node.Next;
                                        SubspaceBuffer toAppend = node.Value;

                                        if (!relGrouper.TryAppend(new ReadOnlySpan<byte>(toAppend.Bytes, 0, toAppend.NumBytes)))
                                        {
                                            break;
                                        }

                                        if (toAppend.CallbackInvoker != null)
                                        {
                                            if (groupedBuffer.CallbackInvoker == null)
                                            {
                                                groupedBuffer.CallbackInvoker = toAppend.CallbackInvoker;
                                            }
                                            else
                                            {
                                                IReliableCallbackInvoker invoker = groupedBuffer.CallbackInvoker;
                                                while (invoker.Next != null)
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

                                    _logManager.LogM(LogLevel.Drivel, nameof(Network), $"Grouped {relGrouper.Count} reliable packets into single reliable packet of {groupedBuffer.NumBytes} bytes.");

                                    LinkedListNode<SubspaceBuffer> groupedNode = _bufferNodePool.Get();
                                    groupedNode.Value = groupedBuffer;
                                    outlist.AddLast(groupedNode);

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
                            ref ReliableHeader header = ref MemoryMarshal.AsRef<ReliableHeader>(b1.Bytes);
                            header.Initialize(conn.s2cn++);

                            b1.NumBytes += ReliableHeader.Length;
                            b1.LastRetry = DateTime.UtcNow.Subtract(new TimeSpan(0, 0, 10));
                            b1.Tries = 0;

                            Interlocked.Increment(ref _globalStats.RelGroupedStats[0]);

                            // Move the node from the unsent queue to the sending/pending queue.
                            conn.UnsentRelOutList.Remove(n1);
                            outlist.AddLast(n1);
                        }

                        // include the "unsent" packets in the outgoing count too
                        outlistlen += conn.UnsentRelOutList.Count;
                    }

                    LinkedListNode<SubspaceBuffer> nextNode;
                    for (LinkedListNode<SubspaceBuffer> node = outlist.First; node != null; node = nextNode)
                    {
                        nextNode = node.Next;

                        SubspaceBuffer buf = node.Value;
                        outlistlen++;

                        // check some invariants
                        ref ReliableHeader rp = ref MemoryMarshal.AsRef<ReliableHeader>(buf.Bytes);

                        if (rp.T1 == 0x00 && rp.T2 == 0x03)
                            Debug.Assert(pri == (int)BandwidthPriority.Reliable);
                        else if (rp.T1 == 0x00 && rp.T2 == 0x04)
                            Debug.Assert(pri == (int)BandwidthPriority.Ack);
                        else
                            Debug.Assert((pri != (int)BandwidthPriority.Reliable) && (pri != (int)BandwidthPriority.Ack));

                        // check if it's time to send this yet (use linearly increasing timeouts)
                        if ((buf.Tries != 0) && ((now - buf.LastRetry).TotalMilliseconds <= (timeout * buf.Tries)))
                            continue;

                        // if we've retried too many times, kick the player
                        if (buf.Tries > _config.MaxRetries)
                        {
                            conn.HitMaxRetries = true;
                            return;
                        }

                        // At this point, there's only one more check to determine if we're sending this packet now: bandwidth limiting.
                        int checkBytes = buf.NumBytes;
                        if (buf.NumBytes > Constants.MaxGroupedPacketItemLength)
                            checkBytes += _config.PerPacketOverhead; // Can't be grouped, so definitely will be sent in its own datagram
                        else if (packetGrouper.Count == 0 || !packetGrouper.CheckAppend(buf.NumBytes))
                            checkBytes += _config.PerPacketOverhead + 2 + 1; // Start of a new grouped packet. So, include an overhead of: IP+UDP header + grouped packet header + grouped packet item header
                        else
                            checkBytes += 1; // Will be appended into a grouped packet (though, not the first in it). So, only include the overhead of the grouped packet item header.

                        // Note for the above checkBytes calcuation:
                        // There is still a chance that at the end, there's only 1 packet remaining to be sent in the packetGrouper.
                        // In which case, when it gets flushed, it will send the individual packet, not grouped.
                        // This means we'd have told the bandwidth limiter 3 bytes more than we actually send, but that's negligible.

                        if (!conn.BandwidthLimiter.Check(
                            checkBytes,
                            (BandwidthPriority)pri))
                        {
                            // try dropping it, if we can
                            if ((buf.Flags & NetSendFlags.Droppable) != 0)
                            {
                                Debug.Assert(pri < (int)BandwidthPriority.Reliable);
                                outlist.Remove(node);
                                _bufferNodePool.Return(node);
                                buf.Dispose();
                                conn.pktdropped++;
                                outlistlen--;
                            }

                            // but in either case, skip it
                            continue;
                        }

                        if (buf.Tries > 0)
                        {
                            // this is a retry, not an initial send. record it for
                            // lag stats and also reduce bw limit (with clipping)
                            retries++;
                            conn.BandwidthLimiter.AdjustForRetry();
                        }

                        buf.LastRetry = DateTime.UtcNow;
                        buf.Tries++;

                        // this sends it or adds it to a pending grouped packet
                        packetGrouper.Send(buf, conn);

                        // if we just sent an unreliable packet, free it so we don't send it again
                        if (pri != (int)BandwidthPriority.Reliable)
                        {
                            outlist.Remove(node);
                            _bufferNodePool.Return(node);
                            buf.Dispose();
                            outlistlen--;
                        }
                    }
                }

                // flush the pending grouped packet
                packetGrouper.Flush(conn);

                conn.retries += (uint)retries;

                if (outlistlen > _config.MaxOutlistSize)
                    conn.HitMaxOutlist = true;
            }

            void SubmitRelStats(Player player)
            {
                if (player == null)
                    return;

                if (_lagCollect == null)
                    return;

                if (!player.TryGetExtraData(_connKey, out ConnData conn))
                    return;

                ReliableLagData rld = new()
                {
                    RelDups = conn.relDups,
                    C2SN = (uint)conn.c2sn,
                    Retries = conn.retries,
                    S2CN = (uint)conn.s2cn,
                };

                _lagCollect.RelStats(player, in rld);
            }

            // call with player status locked
            void ProcessLagouts(Player player, DateTime now, List<Player> toKick, List<Player> toFree)
            {
                if (player == null)
                    throw new ArgumentNullException(nameof(player));

                if (toKick == null)
                    throw new ArgumentNullException(nameof(toKick));

                if (toFree == null)
                    throw new ArgumentNullException(nameof(toFree));

                if (!player.TryGetExtraData(_connKey, out ConnData conn))
                    return;

                // this is used for lagouts and also for timewait
                TimeSpan diff = now - conn.lastPkt;

                // process lagouts
                if (player.WhenLoggedIn == PlayerState.Uninitialized // acts as flag to prevent dups
                    && player.Status < PlayerState.LeavingZone // don't kick them if they're already on the way out
                    && (diff > _config.DropTimeout || conn.HitMaxRetries || conn.HitMaxOutlist)) // these three are our kicking conditions, for now
                {
                    // manually create an unreliable chat packet because we won't have time to send it properly
                    string reason;
                    if (conn.HitMaxRetries)
                        reason = "too many reliable retries";
                    else if (conn.HitMaxOutlist)
                        reason = "too many outgoing packets";
                    else
                        reason = "no data";

                    string message = "You have been disconnected because of lag (" + reason + ").\0"; // it looks like asss sends a null terminated string

                    using SubspaceBuffer buf = _bufferPool.Get();
                    buf.Bytes[0] = (byte)S2CPacketType.Chat;
                    buf.Bytes[1] = (byte)ChatMessageType.SysopWarning;
                    buf.Bytes[2] = 0x00;
                    buf.Bytes[3] = 0x00;
                    buf.Bytes[4] = 0x00;
                    buf.NumBytes = 5 + StringUtils.DefaultEncoding.GetBytes(message, 0, message.Length, buf.Bytes, 5);

                    lock (conn.olmtx)
                    {
                        SendRaw(conn, buf.Bytes.AsSpan(0, buf.NumBytes));
                    }

                    _logManager.LogM(LogLevel.Info, nameof(Network), $"[{player.Name}] [pid={player.Id}] Player kicked for {reason}.");

                    toKick.Add(player);
                }

                // process timewait state
                // status is locked (shared) in here
                if (player.Status == PlayerState.TimeWait)
                {
                    // finally, send disconnection packet
                    Span<byte> disconnectSpan = stackalloc byte[] { 0x00, 0x07 };
                    SendRaw(conn, disconnectSpan);

                    // clear all our buffers
                    lock (conn.bigmtx)
                    {
                        EndSized(player, false);
                    }

                    ClearBuffers(player);

                    // tell encryption to forget about him
                    if (conn.enc != null)
                    {
                        conn.enc.Void(player);
                        _broker.ReleaseInterface(ref conn.enc, conn.iEncryptName);
                    }

                    // log message
                    _logManager.LogM(LogLevel.Info, nameof(Network), $"[{player.Name}] [pid={player.Id}] Disconnected.");

                    if (_clientDictionary.TryRemove(conn.RemoteEndpoint, out _) == false)
                        _logManager.LogM(LogLevel.Error, nameof(Network), "Established connection not in hash table.");

                    toFree.Add(player);
                }
            }

            void ClearBuffers(Player player)
            {
                if (player == null)
                    return;

                if (!player.TryGetExtraData(_connKey, out ConnData conn))
                    return;

                lock (conn.olmtx)
                {
                    // unsent reliable outgoing queue
                    ClearOutgoingQueue(conn.UnsentRelOutList);

                    // regular outgoing queues
                    for (int i = 0; i < conn.outlist.Length; i++)
                    {
                        ClearOutgoingQueue(conn.outlist[i]);
                    }

                    // and presized outgoing
                    foreach (ISizedSendData sd in conn.sizedsends)
                    {
                        sd.RequestData(0, Span<byte>.Empty);
                    }

                    conn.sizedsends.Clear();
                }

                // now clear out the connection's incoming rel buffer
                lock (conn.relmtx)
                {
                    for (int i = 0; i < Constants.CFG_INCOMING_BUFFER; i++)
                    {
                        if (conn.relbuf[i] != null)
                        {
                            conn.relbuf[i].Dispose();
                            conn.relbuf[i] = null;
                        }
                    }
                }

                // and remove from relible signaling queue
                _relqueue.ClearOne(conn);

                void ClearOutgoingQueue(LinkedList<SubspaceBuffer> outlist)
                {
                    LinkedListNode<SubspaceBuffer> nextNode;
                    for (LinkedListNode<SubspaceBuffer> node = outlist.First; node != null; node = nextNode)
                    {
                        nextNode = node.Next;

                        SubspaceBuffer b = node.Value;
                        if (b.CallbackInvoker != null)
                        {
                            QueueReliableCallback(b.CallbackInvoker, player, false);
                            b.CallbackInvoker = null; // the workitem is now responsible for disposing the callback invoker
                        }

                        outlist.Remove(node);
                        _bufferNodePool.Return(node);
                        b.Dispose();
                    }
                }
            }
        }

        private void RelThread()
        {
            while (true)
            {
                // get the next connection that might have packets to process
                ConnData conn = _relqueue.Dequeue();
                if (conn == null)
                    return; // null means stop the thread (module is unloading)

                // then process as much as we can

                lock (conn.relmtx)
                {
                    int processedCount = 0;
                    SubspaceBuffer buf;
                    for (int spot = conn.c2sn % Constants.CFG_INCOMING_BUFFER;
                        (buf = conn.relbuf[spot]) != null && processedCount <= Constants.CFG_INCOMING_BUFFER;
                        spot = conn.c2sn % Constants.CFG_INCOMING_BUFFER)
                    {
                        if (!Monitor.TryEnter(conn.ReliableProcessingLock))
                            break; // another thread is already processing a reliable packet for this connection

                        conn.c2sn++;
                        conn.relbuf[spot] = null;

                        // don't need the mutex while doing the actual processing
                        Monitor.Exit(conn.relmtx);

                        // process it
                        ProcessBuffer(new ReadOnlySpan<byte>(buf.Bytes, ReliableHeader.Length, buf.NumBytes - ReliableHeader.Length), conn);
                        Monitor.Exit(conn.ReliableProcessingLock);
                        buf.Dispose();
                        processedCount++;

                        Monitor.Enter(conn.relmtx);
                    }

                    if (processedCount > Constants.CFG_INCOMING_BUFFER)
                    {
                        if (conn.relbuf[conn.c2sn % Constants.CFG_INCOMING_BUFFER] != null)
                        {
                            // there is more to process, but we don't want to hog all the processing time on 1 connection
                            // re-queue and we'll get back to it
                            _relqueue.Enqueue(conn);
                        }
                    }
                }
            }
        }

        #endregion

        #region Members for MainloopTimer_QueueSizedData

        private byte[] _queueDataBuffer = null;
        private readonly byte[] _queueDataHeader = new byte[6] { 0x00, 0x0A, 0, 0, 0, 0 }; // size of a presized data packet header

        #endregion

        /// <summary>
        /// Attempts to queue up sized send data for each UDP player.
        /// <para>
        /// For each player, it will check if there is sized send work that can be processed (work exists and is not past sized queue limits).
        /// If so, it will retrieve data from the sized send callback (can be partial data), 
        /// break the data into sized send packets (0x0A), 
        /// and add those packets to player's outgoing reliable queue.
        /// </para>
        /// <para>Sized packets are sent reliably so that the other end can reconstruct the data properly.</para>
        /// </summary>
        /// <returns>True, meaning it wants the timer to call it again.</returns>
        private bool MainloopTimer_QueueSizedData()
        {
            int requestAtOnce = _config.PresizedQueuePackets * Constants.ChunkSize;

            byte[] buffer = _queueDataBuffer;
            if (buffer == null || buffer.Length < requestAtOnce + _queueDataHeader.Length)
                buffer = _queueDataBuffer = new byte[requestAtOnce + _queueDataHeader.Length];

            Span<byte> sizedLengthSpan = new(_queueDataHeader, 2, 4);
            _playerData.Lock();

            try
            {
                foreach (Player p in _playerData.Players)
                {
                    if (!IsOurs(p) || p.Status >= PlayerState.TimeWait)
                        continue;

                    if (!p.TryGetExtraData(_connKey, out ConnData conn))
                        continue;

                    if (Monitor.TryEnter(conn.olmtx) == false)
                        continue;

                    if (conn.sizedsends.First != null
                        && conn.outlist[(int)BandwidthPriority.Reliable].Count + conn.UnsentRelOutList.Count < _config.PresizedQueueThreshold)
                    {
                        ISizedSendData sd = conn.sizedsends.First.Value;

                        // unlock while we get the data
                        Monitor.Exit(conn.olmtx);

                        // prepare the header (already has type bytes set, only need to set the length field)
                        BinaryPrimitives.WriteInt32LittleEndian(sizedLengthSpan, sd.TotalLength);

                        // get needed bytes
                        int needed = requestAtOnce;
                        if (sd.TotalLength - sd.Offset < needed)
                            needed = sd.TotalLength - sd.Offset;

                        sd.RequestData(sd.Offset, new Span<byte>(buffer, 6, needed)); // skipping the first 6 bytes for the header
                        sd.Offset += needed;

                        // now lock while we buffer it
                        Monitor.Enter(conn.olmtx);

                        // break the data into sized send (0x0A) packets and queue them up
                        int bufferIndex = 0;
                        while (needed > Constants.ChunkSize)
                        {
                            Array.Copy(_queueDataHeader, 0, buffer, bufferIndex, 6); // write the header in front of the data
                            SendOrBufferPacket(conn, new ReadOnlySpan<byte>(buffer, bufferIndex, Constants.ChunkSize + 6), NetSendFlags.PriorityN1 | NetSendFlags.Reliable);
                            bufferIndex += Constants.ChunkSize;
                            needed -= Constants.ChunkSize;
                        }

                        Array.Copy(_queueDataHeader, 0, buffer, bufferIndex, 6); // write the header in front of the data
                        SendOrBufferPacket(conn, new ReadOnlySpan<byte>(buffer, bufferIndex, needed + 6), NetSendFlags.PriorityN1 | NetSendFlags.Reliable);

                        // check if we need more
                        if (sd.Offset >= sd.TotalLength)
                        {
                            // notify sender that this is the end
                            sd.RequestData(sd.Offset, Span<byte>.Empty);
                            conn.sizedsends.RemoveFirst();
                        }
                    }

                    Monitor.Exit(conn.olmtx);
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            return true;
        }

        /// <summary>
        /// Processes a buffer (1 or more packets) received from a known connection.
        /// </summary>
        /// <param name="data">The buffer to process.</param>
        /// <param name="conn">Context about the connection that the packet is being processed for.</param>
        private void ProcessBuffer(ReadOnlySpan<byte> data, ConnData conn)
        {
            if (conn is null)
                return;

            byte t1 = data[0];

            if (t1 == 0x00)
            {
                byte t2 = data[1];

                // 'core' packet
                if ((t2 < _oohandlers.Length) && (_oohandlers[t2] != null))
                {
                    _oohandlers[t2](data, conn);
                }
                else
                {
                    if (conn.p != null)
                    {
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), $"[{conn.p.Name}] [pid={conn.p.Id}] Unknown network subtype {t2}.");
                    }
                    else
                    {
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), $"(client connection) Unknown network subtype {t2}.");
                    }
                }
            }
            else if (t1 < MAXTYPES)
            {
                SubspaceBuffer buffer = _bufferPool.Get();
                buffer.Conn = conn;
                data.CopyTo(buffer.Bytes);
                buffer.NumBytes = data.Length;

                _mainloop.QueueMainWorkItem(MainloopWork_CallPacketHandlers, buffer); // The workitem disposes the buffer.
            }
            else
            {
                if (conn.p is not null)
                    _logManager.LogM(LogLevel.Malicious, nameof(Network), $"[{conn.p.Name}] [pid={conn.p.Id}] Unknown packet type {t1}.");
                else
                    _logManager.LogM(LogLevel.Malicious, nameof(Network), $"(client connection) Unknown packet type {t1}.");
            }

            void MainloopWork_CallPacketHandlers(SubspaceBuffer buffer)
            {
                if (buffer is null)
                    return;

                try
                {
                    ConnData conn = buffer.Conn;
                    if (conn is null)
                        return;

                    CallPacketHandlers(conn, buffer.Bytes, buffer.NumBytes);
                }
                finally
                {
                    buffer.Dispose();
                }
            }
        }

        private void CallPacketHandlers(ConnData conn, byte[] bytes, int len)
        {
            if (conn == null)
                throw new ArgumentNullException(nameof(conn));

            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));

            if (len < 1)
                throw new ArgumentOutOfRangeException(nameof(len));

            byte packetType = bytes[0];

            if (conn.p != null)
            {
                // player connection
                PacketDelegate handler = null;

                if (packetType < _handlers.Length)
                    handler = _handlers[packetType];

                if (handler == null)
                {
                    _logManager.LogM(LogLevel.Drivel, nameof(Network), $"No handler for packet type 0x{packetType:X2}.");
                    return;
                }

                try
                {
                    handler(conn.p, bytes, len);
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"Handler for packet type 0x{packetType:X2} threw an exception! {ex}.");
                }
            }
            else if (conn.cc != null)
            {
                // client connection
                try
                {
                    conn.cc.Handler.HandlePacket(bytes, len);
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"(client connection) Handler for packet type 0x{packetType:X2} threw an exception! {ex}.");
                }
            }
            else
            {
                _logManager.LogM(LogLevel.Drivel, nameof(Network), $"No player or client connection, but got packet type [0x{packetType:X2}] of length {len}.");
            }
        }

        private void SendToSet(HashSet<Player> set, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            if (set == null)
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
             * 1. foreach player, lock the player's outgoing lock, then while spliting the data into 08/09 packets, buffer into the player's outgoing queue
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
            if (player == null)
                return;

            if (data.Length < 1)
                return;

            if (!IsOurs(player))
                return;

            if (!player.TryGetExtraData(_connKey, out ConnData conn))
                return;

            SendToOne(conn, data, flags);
        }

        private void SendToOne(ConnData conn, ReadOnlySpan<byte> data, NetSendFlags flags)
        {
            if (conn == null)
                return;

            int len = data.Length;
            if (len < 1)
                return;

            bool isReliable = (flags & NetSendFlags.Reliable) == NetSendFlags.Reliable;

            if ((isReliable && len > Constants.MaxPacket - ReliableHeader.Length)
                || (!isReliable && len > Constants.MaxPacket))
            {
                // The data is too large and has to be broken up.
                // Use 00 08 and 00 09 packets (big data packets)
                // send these reliably (to maintain ordering with sequence #)
                Span<byte> bufferSpan = stackalloc byte[Constants.ChunkSize + 2];
                Span<byte> bufferDataSpan = bufferSpan[2..];
                bufferSpan[0] = 0x00;
                bufferSpan[1] = 0x08;

                // Only one 'big' data transfer can be 'buffering up' at a time for a connection.
                // 'Buffering up' meaning from the point the the first 0x00 0x08 packet is buffered till the closing 0x00 0x09 packet is buffered.
                // In other words, no other 0x00 0x08 or 0x00 0x09 packets can be interleaved with the ones we're buffering.
                // Therefore, we hold the lock the entire time we buffer up the 0x00 0x08 or 0x00 0x09 packets.
                lock (conn.olmtx)
                {
                    int position = 0;

                    // First send the 08 packets.
                    while (len > Constants.ChunkSize)
                    {
                        data.Slice(position, Constants.ChunkSize).CopyTo(bufferDataSpan);
                        SendOrBufferPacket(conn, bufferSpan, flags | NetSendFlags.Reliable);
                        position += Constants.ChunkSize;
                        len -= Constants.ChunkSize;
                    }

                    // Final packet is the 09 (signals the end of the big data)
                    // Note: Even if the data fit perfectly into the 08 packets (len == 0), we still need to send the 09 to mark the end.
                    bufferSpan[1] = 0x09;
                    data.Slice(position, len).CopyTo(bufferDataSpan);
                    SendOrBufferPacket(conn, bufferSpan[..(len + 2)], flags | NetSendFlags.Reliable);
                }
            }
            else
            {
                lock (conn.olmtx)
                {
                    SendOrBufferPacket(conn, data, flags);
                }
            }
        }

        private void SendWithCallback(Player player, ReadOnlySpan<byte> data, IReliableCallbackInvoker callbackInvoker)
        {
            if (player == null)
                throw new ArgumentNullException(nameof(player));

            if (data.Length < 1)
                throw new ArgumentOutOfRangeException(nameof(data), "Length must be >= 1.");

            if (callbackInvoker == null)
                throw new ArgumentNullException(nameof(callbackInvoker));

            if (!IsOurs(player))
                return;

            if (!player.TryGetExtraData(_connKey, out ConnData conn))
                return;

            // we can't handle big packets here
            Debug.Assert(data.Length <= (Constants.MaxPacket - ReliableHeader.Length));

            lock (conn.olmtx)
            {
                SendOrBufferPacket(conn, data, NetSendFlags.Reliable, callbackInvoker);
            }
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
            if (conn == null)
                return;

            int len = data.Length;

            Player p = conn.p;

#if CFG_DUMP_RAW_PACKETS
            if (p != null)
                DumpPk($"SEND: {len} bytes to {p.Id}", data);
            else if (conn.cc != null)
                DumpPk($"SEND: {len} bytes to client connection {conn.cc.ServerEndpoint}", data);
#endif

            Span<byte> encryptedBuffer = stackalloc byte[Constants.MaxPacket + 4];
            data.CopyTo(encryptedBuffer);

            if ((p != null) && (conn.enc != null))
            {
                len = conn.enc.Encrypt(p, encryptedBuffer, len);
            }
            else if ((conn.cc != null) && (conn.cc.Encryptor != null))
            {
                len = conn.cc.Encryptor.Encrypt(conn.cc, encryptedBuffer, len);
            }

            if (len == 0)
                return;

            encryptedBuffer = encryptedBuffer[..len];

#if CFG_DUMP_RAW_PACKETS
            if (p != null)
                DumpPk($"SEND: {len} bytes to pid {p.Id} (after encryption)", encryptedBuffer);
            else if (conn.cc != null)
                DumpPk($"SEND: {len} bytes to client connection {conn.cc.ServerEndpoint} (after encryption)", encryptedBuffer);
#endif

            try
            {
                conn.whichSock.SendTo(encryptedBuffer, SocketFlags.None, conn.RemoteEndpoint);
            }
            catch (SocketException ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(Network), $"SocketException with error code {ex.ErrorCode} when sending to {conn.RemoteEndpoint} with game socket {conn.whichSock.LocalEndPoint}. {ex}");
                return;
            }
            catch (Exception ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(Network), $"Exception when sending to {conn.RemoteEndpoint} with game socket {conn.whichSock.LocalEndPoint}. {ex}");
                return;
            }

            conn.bytesSent += (ulong)len;
            conn.pktSent++;
            Interlocked.Add(ref _globalStats.bytesent, (ulong)len);
            Interlocked.Increment(ref _globalStats.pktsent);
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
        private bool SendOrBufferPacket(ConnData conn, ReadOnlySpan<byte> data, NetSendFlags flags, IReliableCallbackInvoker callbackInvoker = null)
        {
            if (conn is null)
                throw new ArgumentNullException(nameof(conn));

            int len = data.Length;
            if (len < 1)
                throw new ArgumentOutOfRangeException(nameof(data), "Length must be at least 1.");

            bool isReliable = (flags & NetSendFlags.Reliable) == NetSendFlags.Reliable;

            //
            // Check some conditions that should be true and would normally be caught when developing in debug mode.
            //

            // data has to be able to fit (a reliable packet has an additional header, so account for that too)
            Debug.Assert((isReliable && len <= Constants.MaxPacket - ReliableHeader.Length) || (!isReliable && len <= Constants.MaxPacket));

            // you can't buffer already-reliable packets
            Debug.Assert(!(len >= 2 && data[0] == 0x00 && data[1] == 0x03));

            // reliable packets can't be droppable
            Debug.Assert((flags & (NetSendFlags.Reliable | NetSendFlags.Droppable)) != (NetSendFlags.Reliable | NetSendFlags.Droppable));

            // If there's a callback, then it must be reliable.
            Debug.Assert(callbackInvoker is null || (flags & NetSendFlags.Reliable) == NetSendFlags.Reliable);

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

            //
            // Check if it can be sent immediately instead of being buffered.
            //

            // try the fast path
            if ((flags & (NetSendFlags.Urgent | NetSendFlags.Reliable)) == NetSendFlags.Urgent)
            {
                // urgent and not reliable
                if (conn.BandwidthLimiter.Check(len + _config.PerPacketOverhead, pri))
                {
                    SendRaw(conn, data);
                    return true;
                }
                else
                {
                    if ((flags & NetSendFlags.Droppable) == NetSendFlags.Droppable)
                    {
                        conn.pktdropped++;
                        return false;
                    }
                }
            }

            //
            // Buffer the packet.
            //

            SubspaceBuffer buf = _bufferPool.Get();
            buf.Conn = conn;
            buf.LastRetry = DateTime.UtcNow.Subtract(new TimeSpan(0, 0, 10));
            buf.Tries = 0;
            buf.CallbackInvoker = callbackInvoker;
            buf.Flags = flags;
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
                conn.outlist[(int)pri].AddLast(node);
            }

            return true;
        }

        private void QueueReliableCallback(IReliableCallbackInvoker callbackInvoker, Player player, bool success)
        {
            if (callbackInvoker == null)
                throw new ArgumentNullException(nameof(callbackInvoker));

            if (player == null)
                throw new ArgumentNullException(nameof(player));

            _mainloop.QueueMainWorkItem(
                MainloopWork_InvokeReliableCallback,
                new InvokeReliableCallbackWork()
                {
                    CallbackInvoker = callbackInvoker,
                    Player = player,
                    Success = success,
                });

            static void MainloopWork_InvokeReliableCallback(InvokeReliableCallbackWork work)
            {
                while (work.CallbackInvoker != null)
                {
                    IReliableCallbackInvoker next = work.CallbackInvoker.Next;

                    using (work.CallbackInvoker)
                    {
                        if (work.Player != null)
                        {
                            work.CallbackInvoker.Invoke(work.Player, work.Success);
                        }
                    }

                    work.CallbackInvoker = next;
                }
            }
        }

        /// <summary>
        /// Logic to run at the end of sized receive.
        /// </summary>
        /// <remarks>Call with <see cref="ConnData.bigmtx"/> locked.</remarks>
        /// <param name="player"></param>
        /// <param name="success"></param>
        private void EndSized(Player player, bool success)
        {
            if (player == null)
                return;

            if (!player.TryGetExtraData(_connKey, out ConnData conn))
                return;

            if (conn.sizedrecv.offset != 0)
            {
                int type = conn.sizedrecv.type;
                int arg = success ? conn.sizedrecv.totallen : -1;

                // tell listeners that they're cancelled
                if (type < MAXTYPES)
                {
                    _sizedhandlers[type]?.Invoke(player, Span<byte>.Empty, arg, arg);
                }

                conn.sizedrecv.type = 0;
                conn.sizedrecv.totallen = 0;
                conn.sizedrecv.offset = 0;
            }
        }

        private void DropConnection(NetClientConnection cc)
        {
            if (cc == null)
                return;

            ReadOnlySpan<byte> disconnectSpan = stackalloc byte[] { 0x00, 0x07 };
            SendRaw(cc.ConnData, disconnectSpan);

            if (cc.Encryptor != null)
            {
                cc.Encryptor.Void(cc);
                _broker.ReleaseInterface(ref cc.Encryptor, cc.EncryptorName);
            }

            _clientConnectionsLock.EnterWriteLock();

            try
            {
                _clientConnections.Remove(cc.ConnData.RemoteEndpoint);
            }
            finally
            {
                _clientConnectionsLock.ExitWriteLock();
            }
        }

        private static bool IsOurs(Player player)
        {
            return player.Type == ClientType.Continuum || player.Type == ClientType.VIE;
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
        /// Context info about a known connection.
        /// </summary>
        /// <remarks>
        /// This can can be a connection for a:
        /// <list type="bullet">
        ///     <item>
        ///         <term><see cref="Player"/></term>
        ///         <description>A client that connected to us, the server. In this case, the object is per-player data.</description>
        ///     </item>
        ///     <item>
        ///         <term><see cref="NetClientConnection"/></term>
        ///         <description>A connection to another server, where we are acting as a client. For example, a connection to a billing server.</description>
        ///     </item>
        /// </list>
        /// </remarks>
        private sealed class ConnData : IPooledExtraData, IDisposable
        {
            /// <summary>
            /// The player this connection is for, or <see langword="null"/> for a client connection.
            /// </summary>
            public Player p;

            /// <summary>
            /// The client this is a part of, or <see langword="null"/> for a player connection.
            /// </summary>
            public NetClientConnection cc;

            /// <summary>
            /// The remote address to communicate with.
            /// </summary>
            public IPEndPoint RemoteEndpoint;

            /// <summary>
            /// which of our sockets to use when sending
            /// </summary>
            public Socket whichSock;

            /// <summary>
            /// sequence number for reliable packets
            /// </summary>
            public int s2cn;

            /// <summary>
            /// sequence number for reliable packets
            /// </summary>
            public int c2sn;

            /// <summary>
            /// time of last packet recvd and of initial connection
            /// </summary>
            public DateTime lastPkt;

            /// <summary>
            /// # of packets sent
            /// </summary>
            public uint pktSent;

            /// <summary>
            /// # of packets received
            /// </summary>
            public uint pktReceived;

            /// <summary>
            /// # of bytes sent
            /// </summary>
            public ulong bytesSent;

            /// <summary>
            /// # of bytes received
            /// </summary>
            public ulong bytesReceived;

            /// <summary>
            /// # of duplicate reliable packets
            /// </summary>
            public uint relDups;

            /// <summary>
            /// # of reliable retries
            /// </summary>
            public uint retries;

            /// <summary>
            /// # of dropped packets
            /// </summary>
            public ulong pktdropped;

            /// <summary>
            /// Whether a reliable packet was retried the maximum # of times.
            /// When set to true, it means the connection should be dropped.
            /// </summary>
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
            public bool HitMaxOutlist;

            /// <summary>
            /// average roundtrip time
            /// </summary>
            public int avgrtt;

            /// <summary>
            /// average roundtrip deviation
            /// </summary>
            public int rttdev;

            /// <summary>
            /// encryption type
            /// </summary>
            public IEncrypt enc;

            /// <summary>
            /// The name of the IEncrypt interface for <see cref="enc"/>.
            /// </summary>
            public string iEncryptName;

            public class SizedReceive
            {
                public int type;
                public int totallen, offset;
            }

            /// <summary>
            /// For receiving sized packets, synchronized with <see cref="bigmtx"/>.
            /// </summary>
            public readonly SizedReceive sizedrecv = new();

            /// <summary>
            /// For receiving big packets, synchronized with <see cref="bigmtx"/>.
            /// </summary>
            public BigReceive BigRecv;

            /// <summary>
            /// For sending sized packets, synchronized with <see cref="olmtx"/>.
            /// </summary>
            public readonly LinkedList<ISizedSendData> sizedsends = new();

            /// <summary>
            /// bandwidth limiting
            /// </summary>
            public IBandwidthLimiter BandwidthLimiter;

            /// <summary>
            /// Array of outgoing lists.  Indexed by <see cref="BandwidthPriority"/>.
            /// </summary>
            public readonly LinkedList<SubspaceBuffer>[] outlist;

            /// <summary>
            /// Unsent outgoing reliable packet queue.
            /// Packets in this queue do not have a sequence number assigned yet.
            /// </summary>
            /// <remarks>
            /// Note: Reliable packets that are in the process of being sent are in <see cref="outlist"/>[<see cref="BandwidthPriority.Reliable"/>].
            /// </remarks>
            public readonly LinkedList<SubspaceBuffer> UnsentRelOutList = new();

            /// <summary>
            /// Incoming reliable packets
            /// </summary>
            public readonly SubspaceBuffer[] relbuf = new SubspaceBuffer[Constants.CFG_INCOMING_BUFFER];

            /// <summary>
            /// mutex for <see cref="outlist"/>
            /// </summary>
            public readonly object olmtx = new();

            /// <summary>
            /// mutex for <see cref="relbuf"/>
            /// </summary>
            public readonly object relmtx = new();

            /// <summary>
            /// This is used to ensure that only one incoming reliable packet is processed at a given time for the connection.
            /// </summary>
            /// <remarks>
            /// Reliable packets need to be processed in the order of their sequence number; two can't be processed simultaneously.
            /// <see cref="relmtx"/> is not held while processing since the receive thread shouldn't be blocked from adding to <see cref="relbuf"/>.
            /// So, this is needed in case there are multiple threads processing reliable packets (Net:ReliableThreads > 1).
            /// </remarks>
            public readonly object ReliableProcessingLock = new();

            /// <summary>
            /// mutex for (<see cref="BigRecv"/> and <see cref="sizedrecv"/>)
            /// </summary>
            public readonly object bigmtx = new();

            public ConnData()
            {
                outlist = new LinkedList<SubspaceBuffer>[(int)Enum.GetValues<BandwidthPriority>().Max() + 1];

                for (int x = 0; x < outlist.Length; x++)
                {
                    outlist[x] = new LinkedList<SubspaceBuffer>();
                }
            }

            public void Initalize(IEncrypt enc, string iEncryptName, IBandwidthLimiter bandwidthLimiter)
            {
                this.enc = enc;
                this.iEncryptName = iEncryptName;
                BandwidthLimiter = bandwidthLimiter ?? throw new ArgumentNullException(nameof(bandwidthLimiter));
                avgrtt = 200; // an initial guess
                rttdev = 100;
                lastPkt = DateTime.UtcNow;
                sizedsends.Clear();

                for (int x = 0; x < outlist.Length; x++)
                {
                    outlist[x].Clear();
                }

                UnsentRelOutList.Clear();
            }

            public void Reset()
            {
                p = null;
                cc = null;
                RemoteEndpoint = null;
                whichSock = null;
                s2cn = 0;
                c2sn = 0;
                lastPkt = default;
                pktSent = 0;
                pktReceived = 0;
                bytesSent = 0;
                bytesReceived = 0;
                relDups = 0;
                retries = 0;
                pktdropped = 0;
                HitMaxRetries = false;
                HitMaxOutlist = false;
                avgrtt = 0;
                rttdev = 0;
                enc = null;
                iEncryptName = null;

                lock (bigmtx)
                {
                    sizedrecv.type = 0;
                    sizedrecv.totallen = 0;
                    sizedrecv.offset = 0;

                    if (BigRecv is not null)
                    {
                        BigRecv.Dispose();
                        BigRecv = null;
                    }
                }

                BandwidthLimiter = null;

                lock (olmtx)
                {
                    sizedsends.Clear();

                    for (int i = 0; i < outlist.Length; i++)
                    {
                        foreach (SubspaceBuffer buffer in outlist[i])
                            buffer.Dispose();

                        outlist[i].Clear();
                    }

                    foreach (SubspaceBuffer buffer in UnsentRelOutList)
                        buffer.Dispose();

                    UnsentRelOutList.Clear();
                }

                lock (relmtx)
                {
                    for (int i = 0; i < relbuf.Length; i++)
                    {
                        if (relbuf[i] is not null)
                        {
                            relbuf[i].Dispose();
                            relbuf[i] = null;
                        }
                    }
                }
            }

            public void Dispose()
            {
                if (BigRecv != null)
                {
                    // make sure any rented arrays are returned to their pool
                    BigRecv.Dispose();
                    BigRecv = null;
                }

                // TODO: return SubspaceBuffer and LinkedListNode<SubspaceBuffer> objects from outlist, UnsentRelOutList, and relbuf to their pools.
            }
        }

        /// <summary>
        /// Represents a connection to a server where this side is acting as the client.
        /// </summary>
        private class NetClientConnection : ClientConnection
        {
            public NetClientConnection(IPEndPoint remoteEndpoint, Socket socket, IClientConnectionHandler handler, IClientEncrypt encryptor, string encryptorName, IBandwidthLimiter bwLimit)
                : base(handler, encryptorName)
            {
                ConnData = new();
                ConnData.cc = this;
                ConnData.RemoteEndpoint = remoteEndpoint ?? throw new ArgumentNullException(nameof(remoteEndpoint));
                ConnData.whichSock = socket ?? throw new ArgumentNullException(nameof(socket));
                ConnData.Initalize(null, null, bwLimit);

                Encryptor = encryptor;
            }

            public ConnData ConnData { get; private set; }

            public IClientEncrypt Encryptor;

            public override EndPoint ServerEndpoint => ConnData.RemoteEndpoint;
        }

        /// <summary>
        /// A specialized data buffer which keeps track of what connection it is for and other useful info.
        /// </summary>
        private class SubspaceBuffer : DataBuffer
        {
            public ConnData Conn;

            public byte Tries;
            public NetSendFlags Flags;

            public DateTime LastRetry;

            public IReliableCallbackInvoker CallbackInvoker;

            public SubspaceBuffer() : base(Constants.MaxPacket + 4) // The extra 4 bytes are to give room for VIE encryption logic to write past.
            {
            }

            public override void Clear()
            {
                Conn = null;
                Tries = 0;
                Flags = NetSendFlags.None;
                LastRetry = DateTime.MinValue;

                if (CallbackInvoker != null)
                {
                    CallbackInvoker.Dispose();
                    CallbackInvoker = null;
                }

                base.Clear();
            }
        }

        /// <summary>
        /// Policy for pooling of <see cref="LinkedListNode{SubspaceBuffer}"/> objects.
        /// </summary>
        private class SubspaceBufferLinkedListNodePooledObjectPolicy : PooledObjectPolicy<LinkedListNode<SubspaceBuffer>>
        {
            public override LinkedListNode<SubspaceBuffer> Create()
            {
                return new LinkedListNode<SubspaceBuffer>(null);
            }

            public override bool Return(LinkedListNode<SubspaceBuffer> obj)
            {
                if (obj is null)
                    return false;

                Debug.Assert(obj.List is null);

                if (obj.List is not null)
                    return false;

                obj.Value = null;
                return true;
            }
        }

        /// <summary>
        /// Interface for an object that represents a callback for when a request to send reliable data has completed (sucessfully or not).
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
            /// <param name="player">The player.</param>
            /// <param name="success">True if the reliable packet was sucessfully sent and acknowledged. False if it was cancelled.</param>
            void Invoke(Player player, bool success);

            /// <summary>
            /// The next reliable callback to invoke in a chain forming linked list.
            /// This is used for reliable packets that have been grouped.
            /// </summary>
            IReliableCallbackInvoker Next
            {
                get;
                set;
            }
        }

        /// <summary>
        /// Represents a callback that has no arguments.
        /// </summary>
        private class ReliableCallbackInvoker : PooledObject, IReliableCallbackInvoker
        {
            private ReliableDelegate _callback;

            public void SetCallback(ReliableDelegate callback)
            {
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            }

            #region IReliableDelegateInvoker Members

            public void Invoke(Player player, bool success)
            {
                _callback?.Invoke(player, success);
            }

            public IReliableCallbackInvoker Next
            {
                get;
                set;
            }

            #endregion

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    _callback = null;
                    Next = null;
                }

                base.Dispose(isDisposing);
            }
        }

        /// <summary>
        /// Represents a callback that has an argument of type <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The type of state to pass to the callback.</typeparam>
        private class ReliableCallbackInvoker<T> : PooledObject, IReliableCallbackInvoker
        {
            private ReliableDelegate<T> _callback;
            private T _clos;

            public void SetCallback(ReliableDelegate<T> callback, T clos)
            {
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
                _clos = clos;
            }

            #region IReliableDelegateInvoker Members

            public void Invoke(Player player, bool success)
            {
                _callback?.Invoke(player, success, _clos);
            }

            public IReliableCallbackInvoker Next
            {
                get;
                set;
            }

            #endregion

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    _callback = null;
                    _clos = default;
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
            /// Requests the sender to provide data.
            /// </summary>
            /// <param name="offset">The position of the data to retrieve.</param>
            /// <param name="dataSpan">The buffer to fill.</param>
            void RequestData(int offset, Span<byte> dataSpan);

            /// <summary>
            /// Total # of bytes of the data to send.
            /// </summary>
            int TotalLength
            {
                get;
            }

            /// <summary>
            /// The current position within the data.
            /// </summary>
            int Offset
            {
                get;
                set;
            }
        }

        /// <summary>
        /// Manages sending sized data (0x00 0x0A).
        /// </summary>
        /// <typeparam name="T"></typeparam>
        private class SizedSendData<T> : ISizedSendData
        {
            private readonly GetSizedSendDataDelegate<T> _requestDataCallback;
            private readonly T _clos;
            private readonly int _totalLength;
            private int _offset;

            public SizedSendData(GetSizedSendDataDelegate<T> requestDataCallback, T clos, int totalLength, int offset)
            {
                _requestDataCallback = requestDataCallback ?? throw new ArgumentNullException(nameof(requestDataCallback));
                _clos = clos;
                _totalLength = totalLength;
                _offset = offset;
            }

            void ISizedSendData.RequestData(int offset, Span<byte> dataSpan)
            {
                _requestDataCallback(_clos, offset, dataSpan);
            }

            int ISizedSendData.TotalLength
            {
                get { return _totalLength; }
            }

            int ISizedSendData.Offset
            {
                get { return _offset; }
                set { _offset = value; }
            }
        }

        /// <summary>
        /// Helper for receiving of 'Big' data streams of the 'Core' protocol, which is the subspace protocol's transport layer.
        /// "Big" data streams consist of zero or more 0x00 0x08 packets followed by a single 0x00 0x09 packet indicating the end.
        /// These packets are sent reliably and therefore are processed in order, effectively being a stream.
        /// </summary>
        /// <remarks>
        /// This class is similar to a <see cref="System.IO.MemoryStream"/>. Though not a stream, and it rents arrays using <see cref="ArrayPool{byte}"/>.
        /// </remarks>
        private class BigReceive : PooledObject
        {
            public byte[] Buffer { get; private set; } = null;
            public int Size { get; private set; } = 0;

            public void Append(ReadOnlySpan<byte> data)
            {
                if (Buffer == null)
                {
                    Buffer = ArrayPool<byte>.Shared.Rent(data.Length);
                }
                else if (Buffer.Length < data.Length)
                {
                    byte[] newBuffer = ArrayPool<byte>.Shared.Rent(Size + data.Length);
                    Array.Copy(Buffer, newBuffer, Size);
                    ArrayPool<byte>.Shared.Return(Buffer, true);
                    Buffer = newBuffer;
                }

                data.CopyTo(Buffer.AsSpan(Size));
                Size += data.Length;
            }

            public void Clear()
            {
                if (Buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(Buffer, true);
                    Buffer = null;
                }

                Size = 0;
            }

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    Clear();
                }

                base.Dispose(isDisposing);
            }
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
            [ConfigHelp("Net", "DropTimeout", ConfigScope.Global, typeof(int), DefaultValue = "3000",
                Description = "How long to get no data from a client before disconnecting him (in ticks).")]
            public TimeSpan DropTimeout { get; private set; }

            /// <summary>
            /// How many S2C packets the server will buffer for a client before dropping him.
            /// </summary>
            [ConfigHelp("Net", "MaxOutlistSize", ConfigScope.Global, typeof(int), DefaultValue = "500",
                Description = "How many S2C packets the server will buffer for a client before dropping him.")]
            public int MaxOutlistSize { get; private set; }

            /// <summary>
            /// if we haven't sent a reliable packet after this many tries, drop the connection
            /// </summary>
            public int MaxRetries { get; private set; }

            /// <summary>
            /// Whether to limit the size of grouped reliable packets to be able to fit into another grouped packet.
            /// </summary>
            public bool LimitReliableGroupingSize { get; private set; }

            /// <summary>
            /// For sized sends (sending files), the threshold to start queuing up more packets.
            /// </summary>
            public int PresizedQueueThreshold { get; private set; }

            /// <summary>
            /// # of additional packets that can be queued up when below the <see cref="PresizedQueueThreshold"/>.
            /// Note: This means, the maximum # of packets that will be queued up at a given time is <see cref="PresizedQueueThreshold"/> - 1 + <see cref="PresizedQueuePackets"/>.
            /// </summary>
            public int PresizedQueuePackets { get; private set; }

            /// <summary>
            /// ip/udp overhead, in bytes per physical packet
            /// </summary>
            public int PerPacketOverhead { get; private set; }

            /// <summary>
            /// How often to refresh the ping packet data.
            /// </summary>
            public TimeSpan PingRefreshThreshold { get; private set; }

            /// <summary>
            /// Display total or playing in simple ping responses.
            /// </summary>
            [ConfigHelp("Net", "SimplePingPopulationMode", ConfigScope.Global, typeof(int), DefaultValue = "1",
                Description =
                "Display what value in the simple ping reponse (used by continuum)?" +
                "1 = display total player count (default);" +
                "2 = display playing count (in ships);" +
                "3 = alternate between 1 and 2")]
            public PingPopulationMode SimplePingPopulationMode { get; private set; }

            public void Load(IConfigManager configManager)
            {
                if (configManager is null)
                    throw new ArgumentNullException(nameof(configManager));

                DropTimeout = TimeSpan.FromMilliseconds(configManager.GetInt(configManager.Global, "Net", "DropTimeout", 3000) * 10);
                MaxOutlistSize = configManager.GetInt(configManager.Global, "Net", "MaxOutlistSize", 500);
                SimplePingPopulationMode = (PingPopulationMode)configManager.GetInt(configManager.Global, "Net", "SimplePingPopulationMode", 1);

                // (deliberately) undocumented settings
                MaxRetries = configManager.GetInt(configManager.Global, "Net", "MaxRetries", 15);
                PresizedQueueThreshold = configManager.GetInt(configManager.Global, "Net", "PresizedQueueThreshold", 5);
                PresizedQueuePackets = configManager.GetInt(configManager.Global, "Net", "PresizedQueuePackets", 25);
                LimitReliableGroupingSize = configManager.GetInt(configManager.Global, "Net", "LimitReliableGroupingSize", 0) != 0;
                PerPacketOverhead = configManager.GetInt(configManager.Global, "Net", "PerPacketOverhead", 28);
                PingRefreshThreshold = TimeSpan.FromMilliseconds(10 * configManager.GetInt(configManager.Global, "Net", "PingDataRefreshTime", 200));
            }
        }

        /// <summary>
        /// Helper that holds population stats that are calculated for responding to pings.
        /// </summary>
        private class PopulationStats
        {
            /// <summary>
            /// Total # of players for the 'virtual' zone.
            /// </summary>
            public uint Total = 0;

            /// <summary>
            /// Total # of players for the 'virtual' zone.
            /// </summary>
            public uint Playing = 0;

            public uint TempTotal = 0;
            public uint TempPlaying = 0;
        }

        /// <summary>
        /// Helper for managing data used for responding to pings.
        /// </summary>
        private class PingData
        {
            public DateTime? LastRefresh = null;

            public readonly PopulationStats Global = new();

            /// <summary>
            /// Key: connectAs
            /// </summary>
            public readonly Dictionary<string, PopulationStats> ConnectAsPopulationStats = new(StringComparer.OrdinalIgnoreCase);

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
            public ulong pcountpings, pktsent, pktrecvd;
            public ulong bytesent, byterecvd;
            public ulong buffercount, buffersused;
            public readonly ulong[] GroupedStats = new ulong[8];
            public readonly ulong[] RelGroupedStats = new ulong[8];
            public readonly ulong[] PriorityStats = new ulong[(int)Enum.GetValues<BandwidthPriority>().Max() + 1];

            public ulong PingsReceived => Interlocked.Read(ref pcountpings);

            public ulong PacketsSent => Interlocked.Read(ref pktsent);

            public ulong PacketsReceived => Interlocked.Read(ref pktrecvd);

            public ulong BytesSent => Interlocked.Read(ref bytesent);

            public ulong BytesReceived => Interlocked.Read(ref byterecvd);

            public ulong BuffersTotal => Interlocked.Read(ref buffercount);

            public ulong BuffersUsed => Interlocked.Read(ref buffersused);

            public ulong GroupedStats0 => Interlocked.Read(ref GroupedStats[0]);
            public ulong GroupedStats1 => Interlocked.Read(ref GroupedStats[1]);
            public ulong GroupedStats2 => Interlocked.Read(ref GroupedStats[2]);
            public ulong GroupedStats3 => Interlocked.Read(ref GroupedStats[3]);
            public ulong GroupedStats4 => Interlocked.Read(ref GroupedStats[4]);
            public ulong GroupedStats5 => Interlocked.Read(ref GroupedStats[5]);
            public ulong GroupedStats6 => Interlocked.Read(ref GroupedStats[6]);
            public ulong GroupedStats7 => Interlocked.Read(ref GroupedStats[7]);

            public ulong RelGroupedStats0 => Interlocked.Read(ref RelGroupedStats[0]);
            public ulong RelGroupedStats1 => Interlocked.Read(ref RelGroupedStats[1]);
            public ulong RelGroupedStats2 => Interlocked.Read(ref RelGroupedStats[2]);
            public ulong RelGroupedStats3 => Interlocked.Read(ref RelGroupedStats[3]);
            public ulong RelGroupedStats4 => Interlocked.Read(ref RelGroupedStats[4]);
            public ulong RelGroupedStats5 => Interlocked.Read(ref RelGroupedStats[5]);
            public ulong RelGroupedStats6 => Interlocked.Read(ref RelGroupedStats[6]);
            public ulong RelGroupedStats7 => Interlocked.Read(ref RelGroupedStats[7]);

            public ulong PriorityStats0 => Interlocked.Read(ref PriorityStats[0]);
            public ulong PriorityStats1 => Interlocked.Read(ref PriorityStats[1]);
            public ulong PriorityStats2 => Interlocked.Read(ref PriorityStats[2]);
            public ulong PriorityStats3 => Interlocked.Read(ref PriorityStats[3]);
            public ulong PriorityStats4 => Interlocked.Read(ref PriorityStats[4]);
        }

        /// <summary>
        /// A helper for grouping up packets to be sent out together as a single combined (0x00 0x0E) packet.
        /// </summary>
        private ref struct PacketGrouper
        {
            private readonly Network _network;
            private readonly Span<byte> _bufferSpan;
            private Span<byte> _remainingSpan;
            private int _count;
            public int Count => _count;
            private int _numBytes;
            public int NumBytes => _numBytes;

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
                if (conn == null)
                    throw new ArgumentNullException(nameof(conn));

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
            public bool CheckAppend(int length)
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
                if (bufferToSend == null)
                    throw new ArgumentNullException(nameof(bufferToSend));

                if (conn == null)
                    throw new ArgumentNullException(nameof(conn));

                if (bufferToSend.NumBytes <= 0)
                    throw new ArgumentException("At least one byte of data is required.", nameof(bufferToSend));

                Send(new ReadOnlySpan<byte>(bufferToSend.Bytes, 0, bufferToSend.NumBytes), conn);
            }

            public void Send(ReadOnlySpan<byte> data, ConnData conn)
            {
                if (data.Length <= 0)
                    throw new ArgumentException("At least one byte of data is required.", nameof(data));

                if (conn == null)
                    throw new ArgumentNullException(nameof(conn));

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
            }
        }

        /// <summary>
        /// State for executing a reliable callback on the mainloop thread.
        /// </summary>
        private struct InvokeReliableCallbackWork
        {
            public IReliableCallbackInvoker CallbackInvoker;
            public Player Player;
            public bool Success;
        }

        /// <summary>
        /// State for executing processing of big data (sent using the the 0x00 0x08 and 0x00 0x09 mechanism) on the mainloop thread.
        /// </summary>
        private struct BigPacketWork
        {
            public ConnData ConnData;
            public BigReceive BigReceive;
        }

        #endregion
    }
}

using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Packets;
using SS.Packets.Game;
using SS.Utilities;
using SS.Utilities.ObjectPool;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        private IObjectPoolManager _objectPoolManager;
        private IPlayerData _playerData;
        private IPrng _prng;
        private InterfaceRegistrationToken<INetwork> _iNetworkToken;
        private InterfaceRegistrationToken<INetworkClient> _iNetworkClientToken;
        private InterfaceRegistrationToken<INetworkEncryption> _iNetworkEncryptionToken;

        private Pool<SubspaceBuffer> _bufferPool;
        private Pool<BigReceive> _bigReceivePool;
        private Pool<ReliableCallbackInvoker> _reliableCallbackInvokerPool;
        private ObjectPool<LinkedListNode<SubspaceBuffer>> _bufferNodePool;
		private readonly DefaultObjectPool<LinkedListNode<ISizedSendData>> _sizedSendDataNodePool = new(new LinkedListNodePooledObjectPolicy<ISizedSendData>(), Constants.TargetPlayerCount);
		private readonly DefaultObjectPool<LinkedListNode<ConnData>> _connDataNodePool = new(new LinkedListNodePooledObjectPolicy<ConnData>(), Constants.TargetPlayerCount);

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
        private readonly Dictionary<SocketAddress, NetClientConnection> _clientConnections = new();
        private readonly ReaderWriterLockSlim _clientConnectionsLock = new(LockRecursionPolicy.NoRecursion);

        private delegate void CorePacketHandler(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags);

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

		#region Sized Send members

		/// <summary>
		/// For synchronizing <see cref="_sizedSendConnections"/> and <see cref="_sizedSendQueue"/>.
		/// </summary>
		private readonly object _sizedSendLock = new();

		/// <summary>
		/// The connections with a sized send in progress.
		/// </summary>
		/// <remarks>
		/// The LinkedListNode reference is to be able to quickly tell if the connection is in the <see cref="_sizedSendQueue"/> without having to iterate over the queue.
		/// </remarks>
		private readonly Dictionary<ConnData, LinkedListNode<ConnData>> _sizedSendConnections = new();

		/// <summary>
		/// Queue containing connections to process.
		/// </summary>
		private readonly LinkedList<ConnData> _sizedSendQueue = new();

		/// <summary>
		/// An event that is signaled when work has been added to the <see cref="_sizedSendQueue"/>.
		/// </summary>
		private readonly AutoResetEvent _sizedSendEvent = new(false);

		#endregion

		// Used to stop the SendThread, ReceiveThread, and SizedSendThread.
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

		/// <summary>
		/// A reusable IPv4 <see cref="SocketAddress"/> to be used only by the <see cref="ReceiveThread"/>.
		/// </summary>
		private SocketAddress _receiveSocketAddressV4 = new(AddressFamily.InterNetwork);

		/// <summary>
		/// A reusable IPv6 <see cref="SocketAddress"/> to be used only by the <see cref="ReceiveThread"/>.
		/// </summary>
		private SocketAddress _receiveSocketAddressV6 = new(AddressFamily.InterNetworkV6);

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
        private readonly ReliableDelegate _sizedSendChunkCompleted;

		public Network()
		{
			// Allocate callback delegates once rather than each time they're used.
			_mainloopWork_CallBigPacketHandlers = MainloopWork_CallBigPacketHandlers;
			_mainloopWork_CallPacketHandlers = MainloopWork_CallPacketHandlers;
            _sizedSendChunkCompleted = SizedSendChunkCompleted;

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
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _prng = prng ?? throw new ArgumentNullException(nameof(prng));

            _config.Load(configManager);

            _connKey = _playerData.AllocatePlayerData<ConnData>();

            _bufferPool = objectPoolManager.GetPool<SubspaceBuffer>();
            _bigReceivePool = objectPoolManager.GetPool<BigReceive>();
            _reliableCallbackInvokerPool = objectPoolManager.GetPool<ReliableCallbackInvoker>();
			_bufferNodePool = new DefaultObjectPool<LinkedListNode<SubspaceBuffer>>(new LinkedListNodePooledObjectPolicy<SubspaceBuffer>(), Constants.TargetPlayerCount * _config.MaxOutlistSize);

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
            for (int i = 0; i < reliableThreadCount; i++)
            {
                thread = new Thread(RelThread) { Name = $"{nameof(Network)}-rel-" + i };
                thread.Start();
                _reliableThreads.Add(thread);
                _threadList.Add(thread);
            }

			// sized send thread
			thread = new(SizedSendThread) { Name = $"{nameof(Network)}-sized-send" };
			thread.Start();
            _threadList.Add(thread);

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

            _playerData.FreePlayerData(ref _connKey);

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

        void INetworkEncryption.ReallyRawSend(SocketAddress remoteAddress, ReadOnlySpan<byte> data, ListenData ld)
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

			Interlocked.Add(ref _globalStats.bytesent, (ulong)data.Length);
			Interlocked.Increment(ref _globalStats.pktsent);
		}

		Player INetworkEncryption.NewConnection(ClientType clientType, IPEndPoint remoteEndpoint, string iEncryptName, ListenData ld)
        {
			ArgumentNullException.ThrowIfNull(remoteEndpoint);
			ArgumentNullException.ThrowIfNull(ld);

			// certain ports may have restrictions on client types
			if ((clientType == ClientType.VIE && !ld.AllowVIE)
                || (clientType == ClientType.Continuum && !ld.AllowContinuum))
            {
                return null;
            }

            // try to find a matching player for the endpoint
            SocketAddress remoteAddress = remoteEndpoint.Serialize();
            if (_playerConnections.TryGetValue(remoteAddress, out Player player))
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
            invoker.SetCallback(callback, ReliableCallbackExecutionOption.Mainloop);
            SendWithCallback(player, data, invoker);
        }

        void INetwork.SendWithCallback<TData>(Player player, ref TData data, ReliableDelegate callback)
        {
            ((INetwork)this).SendWithCallback(player, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref data, 1)), callback);
        }

        void INetwork.SendWithCallback<TState>(Player player, ReadOnlySpan<byte> data, ReliableDelegate<TState> callback, TState clos)
        {
            ReliableCallbackInvoker<TState> invoker = _objectPoolManager.GetPool<ReliableCallbackInvoker<TState>>().Get();
            invoker.SetCallback(callback, clos, ReliableCallbackExecutionOption.Mainloop);
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

			if (!player.TryGetExtraData(_connKey, out ConnData conn))
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
				sizedSendData.Initialize(requestCallback, clos, len);

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
            bool queued = false;

            lock (_sizedSendLock)
            {
                if (!_sizedSendConnections.TryGetValue(conn, out LinkedListNode<ConnData> connNode))
                {
                    connNode = _connDataNodePool.Get();
                    connNode.Value = conn;

                    _sizedSendConnections.Add(conn, connNode);
                    _sizedSendQueue.AddLast(connNode);

                    queued = true;
                }
            }

            if (queued)
            {
                _sizedSendEvent.Set();
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
            if (!_pingData.ConnectAsPopulationStats.TryGetValue(connectAs, out PopulationStats stats))
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
                added = _clientConnections.TryAdd(cc.ConnData.RemoteAddress, cc);
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

        private void CorePacket_KeyResponse(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags)
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

        private void CorePacket_Reliable(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags)
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

            if ((sn - conn.c2sn) >= conn.relbuf.Length || sn < 0)
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
                int spot = sn % conn.relbuf.Length;
                if ((sn < conn.c2sn) || (conn.relbuf[spot] != null))
                {
                    // a dup
                    conn.relDups++;
                }
                else
                {
                    SubspaceBuffer buffer = _bufferPool.Get();
                    buffer.Conn = conn;
                    buffer.ReceiveFlags = flags | NetReceiveFlags.Reliable;
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

        private void CorePacket_Ack(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags)
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
                        ExecuteReliableCallback(b.CallbackInvoker, conn.p, true);
                        b.CallbackInvoker = null;
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

        private void CorePacket_SyncRequest(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags)
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

        private void CorePacket_Drop(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags)
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

        private void CorePacket_BigData(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags)
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
                conn.BigRecv.Flags |= flags | NetReceiveFlags.Big;
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
                        _mainloopWork_CallBigPacketHandlers,
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
        }

		private void MainloopWork_CallBigPacketHandlers(BigPacketWork work)
		{
			try
			{
				if (work.ConnData is null || work.BigReceive is null || work.BigReceive.Buffer is null || work.BigReceive.Size < 1)
					return;

				CallPacketHandlers(work.ConnData, work.BigReceive.Buffer, work.BigReceive.Size, work.BigReceive.Flags);
			}
			finally
			{
				// return the buffer to its pool
				work.BigReceive?.Dispose();
			}
		}

		private void CorePacket_SizedData(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags)
        {
            if (conn is null)
                return;

            if (data.Length < 7)
                return;

            ref readonly SizedHeader header = ref MemoryMarshal.AsRef<SizedHeader>(data);
            int size = header.Size;
            data = data[SizedHeader.Length..];

            lock (conn.bigmtx)
            {
                // only handle sized packets for player connections, not client connections
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
                else if ((conn.sizedrecv.offset + data.Length) > size)
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

        private void CorePacket_CancelSized(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags)
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
                LinkedListNode<ISizedSendData> node = conn.SizedSends.First;
                while (node is not null)
                {
                    LinkedListNode<ISizedSendData> next = node.Next;

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
                bool queued = false;

                lock (_sizedSendLock)
                {
                    if (_sizedSendConnections.TryGetValue(conn, out LinkedListNode<ConnData> node))
                    {
                        if (node.List is null)
                        {
                            _sizedSendQueue.AddLast(node);
                            queued = true;
                        }
					}
                }

                if (queued)
                {
                    _sizedSendEvent.Set();
                }
            }
        }

        private void CorePacket_SizedCancelled(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags)
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

        private void CorePacket_Grouped(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags)
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

                ProcessBuffer(data.Slice(1, len), conn, flags | NetReceiveFlags.Grouped);

                data = data[(1 + len)..];
            }
        }

        private void CorePacket_Special(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags)
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
                _nethandlers[t2]?.Invoke(player, buffer.Bytes, buffer.NumBytes, flags);
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _stopCancellationTokenSource.Dispose();
            _clientConnectionsLock.Dispose();
            _sizedSendEvent.Dispose();
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
                bool isConnectionInitPacket = IsConnectionInitPacket(data);

                // TODO: Add some type of denial of service / flood detection for bad packet sizes? block by ip/port?
                // TODO: Add some type of denial of service / flood detection for repeated connection init attempts over a threshold? block by ip/port?

                // TODO: Distinguish between actual connection init packets and peer packets by checking if the remote endpoint (IP + port) is from a configured peer.
                // Connection init packets should be normal sized game packets, <= Constants.MaxPacket.
                // Peer packets can be much larger than game packets.

                if (isConnectionInitPacket && bytesReceived > Constants.MaxConnInitPacket)
                {
                    _logManager.LogM(LogLevel.Malicious, nameof(Network), $"Received a connection init packet that is too large ({bytesReceived} bytes) from {receivedAddress}.");
                    return;
                }
                else if (!isConnectionInitPacket && bytesReceived > Constants.MaxPacket) // TODO: verify that this is the true maximum, I've read articles that said it was 520 (due to the VIE encryption limit)
                {
                    _logManager.LogM(LogLevel.Malicious, nameof(Network), $"Received a game packet that is too large ({bytesReceived} bytes) from {receivedAddress}.");
                    return;
                }

#if CFG_DUMP_RAW_PACKETS
                DumpPk($"RECV: {bytesReceived} bytes", data);
#endif

                if (!_playerConnections.TryGetValue(receivedAddress, out Player player))
                {
                    // this might be a new connection. make sure it's really a connection init packet
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

                if (!player.TryGetExtraData(_connKey, out ConnData conn))
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
                        ProcessConnectionInit(receivedAddress, data, ld);
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

                ProcessBuffer(data, conn, NetReceiveFlags.None);

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

                if (_pingData.LastRefresh == null
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

                    IArenaManager arenaManager = _broker.GetInterface<IArenaManager>();
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
                                    // Arena summary for ping/info payload
                                    if (arena.Status == ArenaState.Running
                                        && !arena.IsPrivate
                                        && remainingArenaSummary.Length > StringUtils.DefaultEncoding.GetByteCount(arena.Name) + 1 + 2 + 2 + 1) // name + null-terminator + Int16 total + Int16 playing + enough space for a single nul to the series
                                    {
                                        // Name
                                        int bytesWritten = StringUtils.WriteNullTerminatedString(remainingArenaSummary, arena.Name);
                                        remainingArenaSummary = remainingArenaSummary[bytesWritten..];

                                        // Total
                                        BinaryPrimitives.WriteUInt16LittleEndian(remainingArenaSummary, arena.Total > ushort.MaxValue ? ushort.MaxValue : (ushort)arena.Total);
                                        remainingArenaSummary = remainingArenaSummary[2..];

                                        // Playing
                                        BinaryPrimitives.WriteUInt16LittleEndian(remainingArenaSummary, arena.Playing > ushort.MaxValue ? ushort.MaxValue : (ushort)arena.Playing);
                                        remainingArenaSummary = remainingArenaSummary[2..];
                                    }

                                    // Connect As
                                    if (_pingData.ConnectAsPopulationStats.TryGetValue(arena.BaseName, out PopulationStats stats))
                                    {
                                        stats.TempTotal += (uint)arena.Total;
                                        stats.TempPlaying += (uint)arena.Playing;
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
                    IPeer peer = _broker.GetInterface<IPeer>();
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
                        || !_pingData.ConnectAsPopulationStats.TryGetValue(ld.ConnectAs, out PopulationStats stats))
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

                Interlocked.Increment(ref _globalStats.pcountpings);
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
                DumpPk($"RAW CLIENT DATA: {bytesReceived} bytes", data);
#endif

                bool found;
                NetClientConnection cc;

                _clientConnectionsLock.EnterReadLock();

                try
                {
                    found = _clientConnections.TryGetValue(receivedAddress, out cc);
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

                        ProcessBuffer(data, conn, NetReceiveFlags.None);
                    }
                    else
                    {
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), "(client connection) Failed to decrypt packet.");
                    }

                    return;
                }

                _logManager.LogM(LogLevel.Warn, nameof(Network), $"Got data on the client port that was not from any known connection ({receivedAddress}).");
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
				ArgumentNullException.ThrowIfNull(player);
				ArgumentNullException.ThrowIfNull(toKick);
				ArgumentNullException.ThrowIfNull(toFree);

				if (!player.TryGetExtraData(_connKey, out ConnData conn))
                    return;

                TimeSpan diff = now - conn.lastPkt;

                // Process lagouts
                if (player.WhenLoggedIn == PlayerState.Uninitialized // acts as flag to prevent dups
                    && player.Status < PlayerState.LeavingZone // don't kick them if they're already on the way out
                    && (diff > _config.DropTimeout || conn.HitMaxRetries || conn.HitMaxOutlist)) // these three are our kicking conditions, for now
                {
                    string reason;
                    if (conn.HitMaxRetries)
                        reason = "too many reliable retries";
                    else if (conn.HitMaxOutlist)
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

                        lock (conn.olmtx)
                        {
                            SendRaw(conn, chatBytes[..length]);
                        }
					}

                    _logManager.LogM(LogLevel.Info, nameof(Network), $"[{player.Name}] [pid={player.Id}] Player kicked for {reason}.");

                    toKick.Add(player);
                }

                // Process disconnects (timewait state)
                if (player.Status == PlayerState.TimeWait)
                {
                    bool hasSizedSend;
					bool cancelledSizeSend = false;

					lock (conn.SizedSendLock)
                    {
						LinkedListNode<ISizedSendData> node = conn.SizedSends.First;
						hasSizedSend = node is not null;

						// Make sure that all sized sends are cancelled.
						while (node is not null)
						{
							LinkedListNode<ISizedSendData> next = node.Next;

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
                        bool queued = false;

						lock (_sizedSendLock)
						{
							if (_sizedSendConnections.TryGetValue(conn, out LinkedListNode<ConnData> connNode))
							{
								if (connNode.List is null)
								{
									_sizedSendQueue.AddLast(connNode);
                                    queued = true;
								}
							}
						}

                        if (queued)
                        {
                            _sizedSendEvent.Set();
                        }
					}

                    if (hasSizedSend)
                    {
						// Wait for the SizedSendThread to process the sized send(s).
                        // On a later iteration the sized sends will be gone.
						return;
                    }

					// finally, send disconnection packet
					Span<byte> disconnectSpan = [0x00, 0x07];
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

                    if (_playerConnections.TryRemove(conn.RemoteAddress, out _) == false)
                        _logManager.LogM(LogLevel.Error, nameof(Network), "Established connection not in hash table.");

                    toFree.Add(player);
                }
            }

            void ClearBuffers(Player player)
            {
                if (player is null || !player.TryGetExtraData(_connKey, out ConnData conn))
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
				lock (conn.relmtx)
                {
                    for (int i = 0; i < conn.relbuf.Length; i++)
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
                            ExecuteReliableCallback(b.CallbackInvoker, player, false);
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
                if (priorityFilter is not null && pri != (int)priorityFilter.Value)
                    continue;

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
								groupedBuffer.SendFlags = b1.SendFlags; // taking the flags from the first packet, though doesn't really matter since we already know it's reliable
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

                //if (pri == (int)BandwidthPriority.Reliable && conn.p is not null)
                //{
                //    _logManager.LogP(LogLevel.Drivel, nameof(Network), conn.p, $"{DateTime.Now:O} Send iteration");
                //}

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
                        //if (pri == (int)BandwidthPriority.Reliable && conn.p is not null)
                        //{
                        //    _logManager.LogP(LogLevel.Drivel, nameof(Network), conn.p, $"{DateTime.Now:O} Send hit bandwidth limit");
                        //}

                        // try dropping it, if we can
                        if ((buf.SendFlags & NetSendFlags.Droppable) != 0)
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

                    //if (pri == (int)BandwidthPriority.Reliable && conn.p is not null)
                    //{
                    //    _logManager.LogP(LogLevel.Drivel, nameof(Network), conn.p, $"{DateTime.Now:O} Sending reliable data ({buf.NumBytes} bytes)");
                    //}

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
                    for (int spot = conn.c2sn % conn.relbuf.Length;
                        (buf = conn.relbuf[spot]) != null && processedCount <= conn.relbuf.Length;
                        spot = conn.c2sn % conn.relbuf.Length)
                    {
                        if (!Monitor.TryEnter(conn.ReliableProcessingLock))
                            break; // another thread is already processing a reliable packet for this connection

                        conn.c2sn++;
                        conn.relbuf[spot] = null;

                        // don't need the mutex while doing the actual processing
                        Monitor.Exit(conn.relmtx);

                        // process it
                        ProcessBuffer(new ReadOnlySpan<byte>(buf.Bytes, ReliableHeader.Length, buf.NumBytes - ReliableHeader.Length), conn, buf.ReceiveFlags);
                        Monitor.Exit(conn.ReliableProcessingLock);
                        buf.Dispose();
                        processedCount++;

                        Monitor.Enter(conn.relmtx);
                    }

                    if (processedCount > conn.relbuf.Length)
                    {
                        if (conn.relbuf[conn.c2sn % conn.relbuf.Length] != null)
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
			WaitHandle[] waitHandles = [_sizedSendEvent, _stopToken.WaitHandle];

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
                    ConnData conn;

					// Try to dequeue.
					lock (_sizedSendLock)
					{
						LinkedListNode<ConnData> node = _sizedSendQueue.First;
						if (node is null)
						{
							// No work to do.
							break;
						}

						conn = node.Value;
						_sizedSendQueue.Remove(node);
						
                        // Keep in mind that the node is still in _sizedSendConnections.
                        // We'll deal with that later, when the connection has no more sized sends.
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
				// Therefore, ConnData.SizedSendLock is held when accesing the ConnData.SizedSend collection or an ISizedSendData object's cancellation flag.
				//
				// The global player data read lock is taken to access the Player.Status and held to prevent the status from changing while processing.

				Player player = conn.p;
                bool queuedData = false;

                while (true)
                {
                    LinkedListNode<ISizedSendData> sizedSendNode;
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
                        if (conn.SizedSendQueuedCount >= _config.SizedQueueThreshold)
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

								if (player is not null)
								{
									_playerData.Lock();
								}

								try
								{
									lock (conn.SizedSendLock)
									{
										// Now that we reaquired the lock, the sized send should still be the current one since only this thread removes items.
										Debug.Assert(sizedSend == conn.SizedSends.First?.Value);

                                        // Cancel out if the sized send was cancelled while we were requesting data OR if the player's connection is being disconnected.
                                        cancelled = sizedSend.IsCancellationRequested || player.Status == PlayerState.TimeWait;

										if (!cancelled)
										{
											//
											// Queue data
											//

											lock (conn.olmtx)
											{
												// Break the data into sized send (0x0A) packets and queue them up.
												while (bufferSpan.Length > headerSpan.Length)
												{
													Span<byte> packetSpan = bufferSpan[..int.Min(bufferSpan.Length, headerSpan.Length + Constants.ChunkSize)];

													//_logManager.LogP(LogLevel.Drivel, nameof(Network), player, $"{DateTime.Now:O} Queuing sized data ({packetSpan.Length - headerSpan.Length} bytes)");

													// Write the header in front of the data.
													headerSpan.CopyTo(packetSpan);

													// We want to get a callback when we receive an ACK back.
													ReliableCallbackInvoker reliableInvoker = _reliableCallbackInvokerPool.Get();
													reliableInvoker.SetCallback(_sizedSendChunkCompleted, ReliableCallbackExecutionOption.Synchronous);

													Interlocked.Increment(ref conn.SizedSendQueuedCount);
													SendOrBufferPacket(conn, packetSpan, NetSendFlags.PriorityN1 | NetSendFlags.Reliable, reliableInvoker);
													queuedData = true;

													bufferSpan = bufferSpan[(packetSpan.Length - headerSpan.Length)..];
												}
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

							if (conn.SizedSends.Count == 0)
							{
								// No more sized sends for the connection.
								LinkedListNode<ConnData> node;

                                // This is purposely done while continuing to hold the ConnData.SizedSendLock,
                                // so that there will be no conflict if a sized send is being simultaneously added.
								lock (_sizedSendLock)
								{
                                    if (_sizedSendConnections.Remove(conn, out node))
                                    {
                                        if (node.List is not null)
                                        {
                                            _sizedSendQueue.Remove(node);
                                        }
                                    }
								}

								if (node is not null)
								{
									_connDataNodePool.Return(node);
								}
							}
						}

                        if (sendCancellationAck)
                        {
                            // The sized send was cancelled because it was requested by the connection (0x00 0x0B).
                            // This means we are supposed to respond with a sized cancellation ACK (0x00 0x0C),

                            if (player is not null)
                            {
                                _playerData.Lock();
                            }

                            try
                            {
								// Send the sized cancellation ACK packet.
								ReadOnlySpan<byte> cancelSizedAckSpan = [0x00, 0x0C];

                                if (player is not null && player.Status == PlayerState.TimeWait)
                                {
                                    // The connection is being disconnected.
                                    // We can only send it unreliably since the SendThread does not process outgoing queues for connections in this state.
                                    SendRaw(conn, cancelSizedAckSpan);
                                }
                                else
                                {
									lock (conn.olmtx)
                                    {
                                        SendOrBufferPacket(conn, cancelSizedAckSpan, NetSendFlags.Reliable);
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
                }

				return queuedData;
			}

			void SendOutgoingReliable(ConnData conn)
			{
				if (conn is null)
					return;

				Player player = conn.p;

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

						if (Monitor.TryEnter(conn.olmtx))
						{
							try
							{
								SendOutgoing(conn, ref packetGrouper, BandwidthPriority.Reliable);
							}
							finally
							{
								Monitor.Exit(conn.olmtx);
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

		private void SizedSendChunkCompleted(Player player, bool success)
        {
            if (player is null || !player.TryGetExtraData(_connKey, out ConnData connData))
                return;

            //_logManager.LogP(LogLevel.Drivel, nameof(Network), player, $"{DateTime.Now:O} Sized Send Chunk Ack (Success = {success})");

			Interlocked.Decrement(ref connData.SizedSendQueuedCount);

			if (!success)
				return;

            // Make sure the connection is queued to be processed by the SizedSendThread.
			bool queued = false;

            lock (_sizedSendLock)
            {
                if (!_sizedSendConnections.TryGetValue(connData, out LinkedListNode<ConnData> node))
                    return;

                if (node.List is null)
                {
                    // Not already queued, queue it.
                    _sizedSendQueue.AddLast(node);
                    queued = true;
                }
            }

            if (queued)
            {
                _sizedSendEvent.Set();
            }
		}

        /// <summary>
        /// Processes a buffer (1 or more packets) received from a known connection.
        /// </summary>
        /// <param name="data">The buffer to process.</param>
        /// <param name="conn">Context about the connection that the packet is being processed for.</param>
        private void ProcessBuffer(ReadOnlySpan<byte> data, ConnData conn, NetReceiveFlags flags)
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
                    _oohandlers[t2](data, conn, flags);
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
                buffer.ReceiveFlags = flags;
                data.CopyTo(buffer.Bytes);
                buffer.NumBytes = data.Length;

                _mainloop.QueueMainWorkItem(_mainloopWork_CallPacketHandlers, buffer); // The workitem disposes the buffer.
            }
            else
            {
                if (conn.p is not null)
                    _logManager.LogM(LogLevel.Malicious, nameof(Network), $"[{conn.p.Name}] [pid={conn.p.Id}] Unknown packet type {t1}.");
                else
                    _logManager.LogM(LogLevel.Malicious, nameof(Network), $"(client connection) Unknown packet type {t1}.");
            }
        }

		private void MainloopWork_CallPacketHandlers(SubspaceBuffer buffer)
		{
			if (buffer is null)
				return;

			try
			{
				ConnData conn = buffer.Conn;
				if (conn is null)
					return;

				CallPacketHandlers(conn, buffer.Bytes, buffer.NumBytes, buffer.ReceiveFlags);
			}
			finally
			{
				buffer.Dispose();
			}
		}

		private void CallPacketHandlers(ConnData conn, byte[] bytes, int len, NetReceiveFlags flags)
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
                    handler(conn.p, bytes, len, flags);
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
                conn.whichSock.SendTo(encryptedBuffer, SocketFlags.None, conn.RemoteAddress);
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
                conn.outlist[(int)pri].AddLast(node);
            }

            return true;
        }

        private void ExecuteReliableCallback(IReliableCallbackInvoker callbackInvoker, Player player, bool success)
        {
			ArgumentNullException.ThrowIfNull(callbackInvoker);
			ArgumentNullException.ThrowIfNull(player);

            do
            {
                IReliableCallbackInvoker next = callbackInvoker.Next;

				switch (callbackInvoker.ExecutionOption)
                {
                    case ReliableCallbackExecutionOption.Synchronous:
                        InvokeReliableCallback(
                            new InvokeReliableCallbackWork()
                            {
                                CallbackInvoker = callbackInvoker,
                                Player = player,
                                Success = success,
                            });
                        break;

                    case ReliableCallbackExecutionOption.ThreadPool:
                        _mainloop.QueueThreadPoolWorkItem(
                            InvokeReliableCallback,
                            new InvokeReliableCallbackWork()
                            {
                                CallbackInvoker = callbackInvoker,
                                Player = player,
                                Success = success,
                            });
                        break;

                    case ReliableCallbackExecutionOption.Mainloop:
                    default:
                        _mainloop.QueueMainWorkItem(
                            InvokeReliableCallback,
                            new InvokeReliableCallbackWork()
                            {
                                CallbackInvoker = callbackInvoker,
                                Player = player,
                                Success = success,
                            });
                        break;
                }

                callbackInvoker = next;
			}
            while (callbackInvoker is not null);


			static void InvokeReliableCallback(InvokeReliableCallbackWork work)
			{
				using (work.CallbackInvoker)
				{
					if (work.Player is not null)
					{
						work.CallbackInvoker.Invoke(work.Player, work.Success);
					}
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
                _clientConnections.Remove(cc.ConnData.RemoteAddress);
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
        private sealed class ConnData : IResettable, IDisposable
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
			public SocketAddress RemoteAddress;

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
            /// The # of sized send packets that have been queued and not yet ACK'd.
            /// </summary>
            public int SizedSendQueuedCount = 0;

            /// <summary>
            /// For synchronizing <see cref="SizedSends"/>.
            /// </summary>
			public readonly object SizedSendLock = new();

			/// <summary>
			/// Queue of pending sized sends for the connection. Synchronized with <see cref="SizedSendLock"/>.
			/// </summary>
			public readonly LinkedList<ISizedSendData> SizedSends = new();

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
            public readonly SubspaceBuffer[] relbuf;

            /// <summary>
            /// mutex for <see cref="outlist"/> and <see cref="UnsentRelOutList"/>
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

            /// <summary>
            /// Initializes a new instance of the <see cref="ConnData"/> class.
            /// </summary>
            /// <remarks>
            /// This is the constructor that the per-player data uses.
            /// </remarks>
            public ConnData() : this(Constants.IncomingReliableBufferLength_Player)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="ConnData"/> class with a specified length for the incoming reliable data buffer.
            /// </summary>
            /// <remarks>
            /// This is the constructor that the per-player data uses.
            /// </remarks>
            /// <param name="reliableBufferLength">The length of the reliable data buffer (# of reliable packets to allow</param>
            public ConnData(int reliableBufferLength)
            {
                outlist = new LinkedList<SubspaceBuffer>[(int)Enum.GetValues<BandwidthPriority>().Max() + 1];

                for (int x = 0; x < outlist.Length; x++)
                {
                    outlist[x] = new LinkedList<SubspaceBuffer>();
                }

                relbuf = new SubspaceBuffer[reliableBufferLength];
            }

            public void Initalize(IEncrypt enc, string iEncryptName, IBandwidthLimiter bandwidthLimiter)
            {
                this.enc = enc;
                this.iEncryptName = iEncryptName;
                BandwidthLimiter = bandwidthLimiter ?? throw new ArgumentNullException(nameof(bandwidthLimiter));
                avgrtt = 200; // an initial guess
                rttdev = 100;
                lastPkt = DateTime.UtcNow;

                lock (SizedSendLock)
                {
                    SizedSendQueuedCount = 0;
                    SizedSends.Clear();
                }

                lock (olmtx)
                {
                    for (int x = 0; x < outlist.Length; x++)
                    {
                        outlist[x].Clear();
                    }

                    UnsentRelOutList.Clear();
                }
            }

			public bool TryReset()
			{
                p = null;
                cc = null;
                RemoteAddress = null;
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

				lock (SizedSendLock)
                {
					SizedSendQueuedCount = 0;
					SizedSends.Clear();
				}

                lock (olmtx)
                {
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

                return true;
            }

            public void Dispose()
            {
                TryReset();
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
                ConnData = new ConnData(Constants.IncomingReliableBufferLength_ClientConnection);
                ConnData.cc = this;
                ConnData.RemoteAddress = remoteEndpoint.Serialize();
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
            public NetSendFlags SendFlags;
            public NetReceiveFlags ReceiveFlags;

            public DateTime LastRetry;

            public IReliableCallbackInvoker CallbackInvoker;

            public SubspaceBuffer() : base(Constants.MaxPacket + 4) // The extra 4 bytes are to give room for VIE encryption logic to write past.
            {
            }

            public override void Clear()
            {
                Conn = null;
                Tries = 0;
                SendFlags = NetSendFlags.None;
                ReceiveFlags = NetReceiveFlags.None;
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
            /// Option that indicates how the callback should be invoked.
            /// </summary>
            ReliableCallbackExecutionOption ExecutionOption { get; }

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

            public void SetCallback(ReliableDelegate callback, ReliableCallbackExecutionOption executionOption)
            {
                if (!Enum.IsDefined(executionOption))
                    throw new InvalidEnumArgumentException(nameof(executionOption), (int)executionOption, typeof(ReliableCallbackExecutionOption));

				_callback = callback ?? throw new ArgumentNullException(nameof(callback));
                ExecutionOption = executionOption;
            }

            #region IReliableDelegateInvoker Members

            public void Invoke(Player player, bool success)
            {
                _callback?.Invoke(player, success);
            }

            public ReliableCallbackExecutionOption ExecutionOption { get; private set; } = ReliableCallbackExecutionOption.Mainloop;

            public IReliableCallbackInvoker Next { get; set; }

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
        /// Represents a callback that has an argument of type <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The type of state to pass to the callback.</typeparam>
        private class ReliableCallbackInvoker<T> : PooledObject, IReliableCallbackInvoker
        {
            private ReliableDelegate<T> _callback;
            private T _clos;

            public void SetCallback(ReliableDelegate<T> callback, T clos, ReliableCallbackExecutionOption executionOption)
            {
				if (!Enum.IsDefined(executionOption))
					throw new InvalidEnumArgumentException(nameof(executionOption), (int)executionOption, typeof(ReliableCallbackExecutionOption));

				_callback = callback ?? throw new ArgumentNullException(nameof(callback));
                _clos = clos;
				ExecutionOption = executionOption;
			}

            #region IReliableDelegateInvoker Members

            public void Invoke(Player player, bool success)
            {
                _callback?.Invoke(player, success, _clos);
            }

			public ReliableCallbackExecutionOption ExecutionOption { get; private set; } = ReliableCallbackExecutionOption.Mainloop;

			public IReliableCallbackInvoker Next { get; set; }

            #endregion

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    _callback = null;
                    _clos = default;
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
			/// <param name="dataSpan">The buffer to fill. An empty buffer indicates that the send is finishd (completed or cancelled).</param>
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
            public static readonly ObjectPool<SizedSendData<TState>> Pool = new DefaultObjectPool<SizedSendData<TState>>(new DefaultPooledObjectPolicy<SizedSendData<TState>>(), 256);

            private GetSizedSendDataDelegate<TState> _requestDataCallback;
            private TState _state;
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

				_requestDataCallback(_state, _offset, dataSpan);
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
		/// Helper for receiving of 'Big' data streams of the 'Core' protocol, which is the subspace protocol's transport layer.
		/// "Big" data streams consist of zero or more 0x00 0x08 packets followed by a single 0x00 0x09 packet indicating the end.
		/// These packets are sent reliably and therefore are processed in order, effectively being a stream.
		/// </summary>
		/// <remarks>
		/// This class is similar to a <see cref="System.IO.MemoryStream"/>. Though not a stream, and it rents arrays using <see cref="ArrayPool{byte}"/>.
		/// </remarks>
		private class BigReceive : PooledObject
        {
            public NetReceiveFlags Flags = NetReceiveFlags.None;
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
                Flags = NetReceiveFlags.None;

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
            /// Display total or playing in simple ping responses.
            /// </summary>
            [ConfigHelp("Net", "SimplePingPopulationMode", ConfigScope.Global, typeof(PingPopulationMode), DefaultValue = "1",
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

				DropTimeout = TimeSpan.FromMilliseconds(configManager.GetInt(configManager.Global, "Net", "DropTimeout", 3000) * 10);
                MaxOutlistSize = configManager.GetInt(configManager.Global, "Net", "MaxOutlistSize", 500);
                SimplePingPopulationMode = configManager.GetEnum(configManager.Global, "Net", "SimplePingPopulationMode", PingPopulationMode.Total);

                // (deliberately) undocumented settings
                MaxRetries = configManager.GetInt(configManager.Global, "Net", "MaxRetries", 15);
                SizedQueueThreshold = int.Clamp(configManager.GetInt(configManager.Global, "Net", "PresizedQueueThreshold", 5), 1, MaxOutlistSize);
                SizedQueuePackets = int.Clamp(configManager.GetInt(configManager.Global, "Net", "PresizedQueuePackets", 25), 1, MaxOutlistSize);
                SizedSendOutgoing = configManager.GetInt(configManager.Global, "Net", "SizedSendOutgoing", 0) != 0;
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
            private readonly object _lock = new();

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
        /// Though only the mainloop thread mutates the dictionary (duing Load and Unload), 
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

				Interlocked.Increment(ref _network._globalStats.GroupedStats[0]);
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

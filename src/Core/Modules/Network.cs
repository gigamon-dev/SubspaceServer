using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// TODO: revisit this module, it is basically 95% a direct port from asss
    /// </summary>
    [CoreModuleInfo]
    public class Network : IModule, IModuleLoaderAware, INetwork, INetworkEncryption, INetworkClient, IDisposable
    {
        /// <summary>
        /// specialized data buffer which keeps track of what connection it is for and other useful info
        /// </summary>
        private class SubspaceBuffer : DataBuffer
        {
            internal ConnData Conn;

            public byte Tries;
            public NetSendFlags Flags;

            public DateTime LastRetry;

            public IReliableCallbackInvoker CallbackInvoker;

            public override void Clear()
            {
                Conn = null;
                Tries = 0;
                Flags = NetSendFlags.None;
                LastRetry = DateTime.MinValue;
                CallbackInvoker = null;

                base.Clear();
            }
        }
        
        private interface IReliableCallbackInvoker
        {
            void Invoke(Player p, bool success);
        }

        private class ReliableCallbackInvoker : IReliableCallbackInvoker
        {
            private readonly ReliableDelegate callback;

            public ReliableCallbackInvoker(ReliableDelegate callback)
            {
                this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
            }

            #region IReliableDelegateInvoker Members

            public void Invoke(Player p, bool success)
            {
                callback(p, success);
            }

            #endregion
        }

        private class ReliableCallbackInvoker<T> : IReliableCallbackInvoker
        {
            private readonly ReliableDelegate<T> callback;
            private readonly T clos;

            public ReliableCallbackInvoker(ReliableDelegate<T> callback, T clos)
            {
                this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
                this.clos = clos;
            }

            #region IReliableDelegateInvoker Members

            public void Invoke(Player p, bool success)
            {
                callback(p, success, clos);
            }

            #endregion
        }
        
        private readonly Pool<SubspaceBuffer> _bufferPool = Pool<SubspaceBuffer>.Default;

        private interface ISizedSendData
        {
            void RequestData(int offset, byte[] buf, int bufStartIndex, int bytesNeeded);

            int TotalLength
            {
                get;
            }

            int Offset
            {
                get;
                set;
            }
        }

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

            void ISizedSendData.RequestData(int offset, byte[] buf, int bufStartIndex, int bytesNeeded)
            {
                _requestDataCallback(_clos, offset, buf, bufStartIndex, bytesNeeded);
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

        private class ConnData
        {
            /// <summary>
            /// the player this connection is for, or NULL for a client connection
            /// </summary>
            public Player p;

            /// <summary>
            /// the client this is a part of, or NULL for a player connection
            /// </summary>
            public ClientConnection cc;

            /// <summary>
            /// the address to send packets to
            /// </summary>
            public IPEndPoint sin;

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
            /// # of packets recieved
            /// </summary>
            public uint pktRecieved;

            /// <summary>
            /// # of bytes sent
            /// </summary>
            public ulong bytesSent;

            /// <summary>
            /// # of bytes recieved
            /// </summary>
            public ulong bytesRecieved;

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

            public bool hitmaxretries;
            public bool hitmaxoutlist;
            //byte unused1;
            //byte unused2;

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

            /// <summary>
            /// For receiving sized packets, protected by bigmtx
            /// </summary>
            internal class SizedRecieve
            {
                public int type;
                public int totallen, offset;
            }

            /// <summary>
            /// For receiving big packets, protected by bigmtx
            /// </summary>
            public SizedRecieve sizedrecv = new SizedRecieve();

            internal class BigRecieve
            {
                public int size, room;
                public byte[] buf; //byte *buf; in asss

                internal void Free()
                {
                    buf = null;
                    size = 0;
                    room = 0;
                }
            }

            /// <summary>
            /// stuff for recving big packets, protected by bigmtx
            /// </summary>
            public readonly BigRecieve bigrecv = new BigRecieve();

            /// <summary>
            /// stuff for sending sized packets, protected by olmtx
            /// </summary>
            public LinkedList<ISizedSendData> sizedsends = new LinkedList<ISizedSendData>();

            /// <summary>
            /// bandwidth limiting
            /// </summary>
            public IBWLimit bw;

            /// <summary>
            /// array of outgoing lists, one for each priority
            /// </summary>
            public LinkedList<SubspaceBuffer>[] outlist = new LinkedList<SubspaceBuffer>[(int)BandwidthPriorities.NumPriorities];

            /// <summary>
            /// incoming reliable packets
            /// </summary>
            public SubspaceBuffer[] relbuf = new SubspaceBuffer[Constants.CFG_INCOMING_BUFFER];

            /// <summary>
            /// mutex for <see cref="outlist"/>
            /// </summary>
            public object olmtx = new object();

            /// <summary>
            /// mutex for <see cref="relbuf"/>
            /// </summary>
            public object relmtx = new object();

            /// <summary>
            /// mutex for (<see cref="bigrecv"/> and <see cref="sizedrecv"/>)
            /// </summary>
            public object bigmtx = new object();

            public void Initalize(IEncrypt enc, string iEncryptName, IBWLimit bw)
            {
                this.enc = enc;
                this.iEncryptName = iEncryptName;
                this.bw = bw;
                avgrtt = 200; // an initial guess
                rttdev = 100;
                lastPkt = DateTime.UtcNow;
                sizedsends.Clear();

                for (int x = 0; x < outlist.Length; x++)
                {
                    outlist[x] = new LinkedList<SubspaceBuffer>();
                }
            }
        }

        private class ClientConnection : BaseClientConnection
        {
            public ConnData c;

            public ClientConnection(IClientConn i, IClientEncrypt enc)
                : base(i, enc)
            {
            }
        }

        private ComponentBroker _broker;
        private IPlayerData _playerData;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IMainloop _mainloop;
        private IMainloopTimer _mainloopTimer;
        private IBandwidthLimit _bandwithLimit;
        private ILagCollect _lagCollect;
        private InterfaceRegistrationToken _iNetworkToken;
        private InterfaceRegistrationToken _iNetworkClientToken;
        private InterfaceRegistrationToken _iNetworkEncryptionToken;

        [Flags]
        private enum PingPopulationMode
        {
            Total = 1,
            Playing = 2,
        }

        private class Config
        {
            public int droptimeout;
            public int maxoutlist;

            /// <summary>
            /// if we haven't sent a reliable packet after this many tries, drop the connection
            /// </summary>
            public int maxretries;

            /// <summary>
            /// threshold to start queuing up more packets, and packets to queue up, for sending files
            /// </summary>
            public int queue_threshold, queue_packets;

            /// <summary>
            /// ip/udp overhead, in bytes per physical packet
            /// </summary>
            public int overhead;

            /// <summary>
            /// how often to refresh the ping packet data
            /// </summary>
            public TimeSpan PingRefreshThreshold;

            /// <summary>
            /// display total or playing in simple ping responses
            /// </summary>
            public PingPopulationMode simplepingpopulationmode;
        }

        private readonly Config _config = new Config();

        private class PingData
        {
            public DateTime? LastRefresh = null;
            public uint GlobalTotal = 0;
            public uint GlobalPlaying = 0;
        }

        private readonly PingData _pingData = new PingData();

        /// <summary>
        /// per player data key to ConnData
        /// </summary>
        private int _connKey;

        private readonly Dictionary<EndPoint, Player> _clienthash = new Dictionary<EndPoint, Player>();
        private readonly object _hashmtx = new object();
        
        /// <summary>
        /// this is a helper to group up packets to be send out together as 1 combined packet
        /// </summary>
        private struct GroupedPacket
        {
            private readonly Network _network;
            private readonly byte[] _buf;
            private ArraySegment<byte> _remainingSegment;
            private int _count;

            public GroupedPacket(Network network, byte[] buf)
            {
                _network = network;
                _buf = buf;

                _buf[0] = 0x00;
                _buf[1] = 0x0E;
                _remainingSegment = new ArraySegment<byte>(_buf, 2, Math.Min(_buf.Length, Constants.MaxPacket) - 2);
                _count = 0;
            }

            public void Init()
            {
                _buf[0] = 0x00;
                _buf[1] = 0x0E;
                _remainingSegment = new ArraySegment<byte>(_buf, 2, Math.Min(_buf.Length, Constants.MaxPacket) - 2);
                _count = 0;
            }

            public void Flush(ConnData conn)
            {
                if (_count == 1)
                {
                    // there's only one in the group, so don't send it
                    // in a group. +3 to skip past the 00 0E and size of
                    // first packet
                    _network.SendRaw(conn, new ArraySegment<byte>(_buf, 3, _remainingSegment.Offset - 3));
                }
                else if (_count > 1)
                {
                    _network.SendRaw(conn, _buf, _remainingSegment.Offset);
                }

                // record stats about grouped packets
                if (_count > 0)
                    _network._globalStats.grouped_stats[Math.Min((_count - 1), _network._globalStats.grouped_stats.Length - 1)]++;

                Init();
            }

            public void Send(SubspaceBuffer buffer, ConnData conn)
            {
#if DISABLE_GROUPED_SEND
                _network.sendRaw(conn, buffer.Bytes, buffer.NumBytes);
#else
                if (buffer.NumBytes <= 255) // 255 must be the size limit a grouped packet can store (max 1 byte can represent for the length)
                {
                    //if (_remainingSegment.Count > (Constants.MaxPacket - 10 - buffer.NumBytes)) // I don't know why asss does the -10
                    if (_remainingSegment.Count < (buffer.NumBytes + 1)) // +1 is for the byte that specifies the length
                        Flush(conn); // not enough room in the grouped packet, send it out first to start with a fresh grouped packet

                    _remainingSegment.Array[_remainingSegment.Offset] = (byte)buffer.NumBytes;
                    Array.Copy(buffer.Bytes, 0, _remainingSegment.Array, _remainingSegment.Offset + 1, buffer.NumBytes);

                    _remainingSegment = new ArraySegment<byte>(_remainingSegment.Array, _remainingSegment.Offset + (buffer.NumBytes + 1), _remainingSegment.Count - (buffer.NumBytes + 1));
                    _count++;
                }
                else
                {
                    // can't fit into a grouped packet, send immediately
                    _network.SendRaw(conn, buffer.Bytes, buffer.NumBytes);
                }
#endif
            }
        }

        private delegate void oohandler(SubspaceBuffer buffer);

        /// <summary>
        /// handlers for 'core' packets (ss protocol's network/transport layer)
        /// </summary>
        private readonly oohandler[] _oohandlers;

        private const int MAXTYPES = 64;

        /// <summary>
        /// handlers for 'game' packets
        /// </summary>
        private readonly PacketDelegate[] _handlers = new PacketDelegate[MAXTYPES];
        private readonly PacketDelegate[] _nethandlers = new PacketDelegate[0x14];
        private readonly SizedPacketDelegate[] _sizedhandlers = new SizedPacketDelegate[MAXTYPES];

        private const int MICROSECONDS_PER_MILLISECOND = 1000;

        private readonly MessagePassingQueue<ConnData> _relqueue = new MessagePassingQueue<ConnData>();

        private readonly CancellationTokenSource _stopCancellationTokenSource = new CancellationTokenSource();
        private CancellationToken _stopToken;

        private readonly List<Thread> _threadList = new List<Thread>();
        private readonly List<Thread> _reliableThreads = new List<Thread>();

        /// <summary>
        /// info about sockets this object has created, etc...
        /// </summary>
        private readonly List<ListenData> _listenDataList;
        private readonly ReadOnlyCollection<ListenData> _readOnlyListenData;

        /// <summary>
        /// Key: connectAs
        /// </summary>
        /// <remarks>for now, only 1 listen data allowed for each connectAs.</remarks>
        private readonly Dictionary<string, ListenData> _listenConnectAsLookup = new Dictionary<string, ListenData>();

        private Socket _clientSocket;

        // TODO: figure out if multiple threads are reading/writing
        private class NetStats : IReadOnlyNetStats
        {
            public ulong pcountpings, pktsent, pktrecvd;
            public ulong bytesent, byterecvd;
            public ulong buffercount, buffersused;
            public ulong[] grouped_stats = new ulong[8];
            public ulong[] pri_stats = new ulong[5];

            public ulong PingsReceived => pcountpings;

            public ulong PacketsSent => pktsent;

            public ulong PacketsReceived => pktrecvd;

            public ulong BytesSent => bytesent;

            public ulong BytesReceived => byterecvd;

            public ulong BuffersTotal => buffercount;

            public ulong BuffersUsed => buffersused;

            public ReadOnlySpan<ulong> GroupedStats => grouped_stats;

            public ReadOnlySpan<ulong> PriorityStats => pri_stats;
        }

        private readonly NetStats _globalStats = new NetStats();

        public Network()
        {
            _oohandlers = new oohandler[20];

            _oohandlers[0] = null; //00 - nothing
            _oohandlers[1] = null; //01 - key initiation
            _oohandlers[2] = ProcessKeyResponse; //02 - key response
            _oohandlers[3] = ProcessReliable; //03 - reliable
            _oohandlers[4] = ProcessAck; //04 - reliable response
            _oohandlers[5] = ProcessSyncRequest; //05 - time sync request
            _oohandlers[6] = null; //06 - time sync response
            _oohandlers[7] = ProcessDrop; //07 - close connection
            _oohandlers[8] = ProcessBigData; //08 - bigpacket
            _oohandlers[9] = ProcessBigData; //09 - bigpacket2
            _oohandlers[10] = ProcessPresize; //0A - presized data (file transfer)
            _oohandlers[11] = ProcessCancelReq;  //0B - cancel presized
            _oohandlers[12] = ProcessCancel; //0C - presized has been cancelled
            _oohandlers[13] = null; //0D - nothing
            _oohandlers[14] = ProcessGrouped; //0E - grouped
            _oohandlers[15] = null; // 0x0F
            _oohandlers[16] = null; // 0x10
            _oohandlers[17] = null; // 0x11
            _oohandlers[18] = null; // 0x12
            _oohandlers[19] = ProcessSpecial; // 0x13 - cont key response

            _listenDataList = new List<ListenData>();
            _readOnlyListenData = new ReadOnlyCollection<ListenData>(_listenDataList);
        }

        #region Socket initialization

        private Socket CreateSocket(int port, IPAddress bindAddress)
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

#if NETFRAMEWORK
            // to prevent the exception "An existing connection was forcibly closed by the remote host"
            // http://support.microsoft.com/kb/263823
            const int SIO_UDP_CONNRESET = -1744830452;  // since IOControl() takes int instead of uint
            byte[] optionInValue = new byte[] { 0, 0, 0, 0 };
            socket.IOControl(SIO_UDP_CONNRESET, optionInValue, null);
#endif

            try
            {
                socket.Blocking = false;
            }
            catch (Exception ex)
            {
                // not fatal, just warn
                _logManager.LogM(LogLevel.Warn, nameof(Network), "can't make socket nonblocking: {0}", ex.Message);
            }

            if (bindAddress == null)
                bindAddress = IPAddress.Any;

            try
            {
                socket.Bind(new IPEndPoint(bindAddress, port));
            }
            catch (Exception ex)
            {
                socket.Close();
                throw new Exception(string.Format("cannot bind socket to {0}:{1}", bindAddress, port), ex);
            }

            return socket;
        }

        private ListenData CreateListenDataSockets(int configIndex)
        {
            string configSection = "Listen" + ((configIndex == 0) ? string.Empty : configIndex.ToString());

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
                _logManager.LogM(LogLevel.Error, nameof(Network), "unable to create game socket: {0}", ex.Message);
                return null;
            }

            try
            {
                pingSocket = CreateSocket(pingPort, bindAddress);
            }
            catch (Exception ex)
            {
                gameSocket.Close();
                _logManager.LogM(LogLevel.Error, nameof(Network), "unable to create ping socket: {0}", ex.Message);
                return null;
            }

            ListenData listenData = new ListenData(gameSocket, pingSocket);

            listenData.AllowVIE = _configManager.GetInt(_configManager.Global, configSection, "AllowVIE", 1) > 0;
            listenData.AllowContinuum = _configManager.GetInt(_configManager.Global, configSection, "AllowCont", 1) > 0;
            listenData.ConnectAs = _configManager.GetStr(_configManager.Global, configSection, "ConnectAs");

            return listenData;
        }

        private bool InitializeSockets()
        {
            //
            // Listen sockets (pairs of game and ping sockets)
            //

            for (int x = 0; x < 10; x++)
            {
                ListenData listenData = CreateListenDataSockets(x);
                if (listenData == null)
                    continue;

                _listenDataList.Add(listenData);

                if (string.IsNullOrEmpty(listenData.ConnectAs) == false)
                {
                    _listenConnectAsLookup.Add(listenData.ConnectAs, listenData);
                }

                _logManager.LogM(LogLevel.Drivel, nameof(Network), "listening on {0}", listenData.GameSocket.LocalEndPoint);
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
                _logManager.LogM(LogLevel.Error, nameof(Network), "unable to create socket for client connections: {0}", ex.Message);
            }

            return true;
        }

        #endregion

        private void ReceiveThread()
        {
            List<Socket> socketList = new List<Socket>(_listenDataList.Count * 2 + 1);
            List<Socket> checkReadList = new List<Socket>(_listenDataList.Count * 2 + 1);
            
            Dictionary<EndPoint, (char Type, ListenData ListenData)> endpointLookup 
                = new Dictionary<EndPoint, (char type, ListenData listenData)>(_listenDataList.Count * 2);

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
                                HandleGamePacketRecieved(tuple.ListenData);
                            else if (tuple.Type == 'P')
                                HandlePingPacketRecieved(tuple.ListenData);
                        }
                        else if (socket == _clientSocket)
                        {
                            HandleClientPacketRecieved();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(LogManager), "Caught an exception in ReceiveThread. {0}", ex);
                }
            }
        }

        private void SendThread()
        {
            while (_stopToken.IsCancellationRequested == false)
            {
                LinkedList<Player> toKill = new LinkedList<Player>();
                LinkedList<Player> toFree = new LinkedList<Player>();

                // first send outgoing packets (players)
                _playerData.Lock();

                try
                {
                    foreach (Player p in _playerData.PlayerList)
                    {
                        if (p.Status >= PlayerState.Connected &&
                            p.Status < PlayerState.TimeWait &&
                            IsOurs(p))
                        {
                            if (!(p[_connKey] is ConnData conn))
                                continue;

                            if (Monitor.TryEnter(conn.olmtx))
                            {
                                try
                                {
                                    SendOutgoing(conn);
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
                    foreach (Player p in _playerData.PlayerList)
                    {
                        if (p.Status >= PlayerState.Connected &&
                            IsOurs(p))
                        {
                            ProcessLagouts(p, now, toKill, toFree);
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                // now kill the ones we needed to above
                foreach (Player p in toKill)
                {
                    _playerData.KickPlayer(p);
                }
                toKill.Clear();

                // and free ...
                foreach (Player p in toFree)
                {
                    if (!(p[_connKey] is ConnData))
                        continue;

                    // one more time, just to be sure
                    ClearBuffers(p);

                    //bwlimit->Free(conn->bw);

                    _playerData.FreePlayer(p);
                }
                toFree.Clear();

                // outgoing packets and lagouts for client connections
                // TODO

                if (_stopToken.IsCancellationRequested)
                    return;

                Thread.Sleep(10); // 1/100 second
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
                    SubspaceBuffer buf;
                    for (int spot = conn.c2sn % Constants.CFG_INCOMING_BUFFER;
                        (buf = conn.relbuf[spot]) != null;
                        spot = (spot + 1) % Constants.CFG_INCOMING_BUFFER)
                    {
                        conn.c2sn++;
                        conn.relbuf[spot] = null;

                        // don't need the mutex while doing the actual processing
                        Monitor.Exit(conn.relmtx);

                        // process it
                        buf.NumBytes -= Constants.ReliableHeaderLen;
                        Array.Copy(buf.Bytes, Constants.ReliableHeaderLen, buf.Bytes, 0, buf.NumBytes);
                        ProcessBuffer(buf);

                        Monitor.Enter(conn.relmtx);
                    }
                }
            }
        }

        private byte[] _queueDataBuffer = null;
        private readonly byte[] _queueDataHeader = new byte[6] { 0x00, 0x0A, 0, 0, 0, 0 }; // size of a presized data packet header

        // NOTE: this is optimized slightly more than asss in the sense that it doesn't copy the array to another buffer just to send it off to bufferPacket()
        private bool QueueMoreData()
        {
            int requestAtOnce = _config.queue_packets * Constants.ChunkSize;

            byte[] buffer = _queueDataBuffer;
            if (buffer == null || buffer.Length < requestAtOnce + _queueDataHeader.Length)
                buffer = _queueDataBuffer = new byte[requestAtOnce + _queueDataHeader.Length];

            // NOTE: asss allocates the buffer on the stack, which can be done here as well using stackalloc in an unsafe context
            // however, i don't see much of a benefit to using unsafe code here
            // if performance becomes a problem, then this can be changed

            ReliablePacket packet = new ReliablePacket(_queueDataHeader);

            _playerData.Lock();
            try
            {
                foreach (Player p in _playerData.PlayerList)
                {
                    if(!IsOurs(p) || p.Status >= PlayerState.TimeWait)
                        continue;

                    if (!(p[_connKey] is ConnData conn))
                        continue;

                    if (Monitor.TryEnter(conn.olmtx) == false)
                        continue;

                    if (conn.sizedsends.First != null &&
                        conn.outlist[(int)BandwidthPriorities.Reliable].Count < _config.queue_threshold)
                    {
                        ISizedSendData sd = conn.sizedsends.First.Value;

                        // unlock while we get the data
                        Monitor.Exit(conn.olmtx);

                        // prepare packet
                        //packet.T1 = 0x00;
                        //packet.T2 = 0x0A; // this is really a sized packet
                        packet.SeqNum = sd.TotalLength; // header packet already has type bytes set, only need to set the length field

                        // get needed bytes
                        int needed = requestAtOnce;
                        if (sd.TotalLength - sd.Offset < needed)
                            needed = sd.TotalLength - sd.Offset;

                        sd.RequestData(sd.Offset, buffer, 6, needed); // skipping the first 6 bytes for the header
                        sd.Offset += needed;

                        // now lock while we buffer it
                        Monitor.Enter(conn.olmtx);

                        // put data in outlist, in 480 byte chunks
                        int bufferIndex = 0;
                        while (needed > Constants.ChunkSize)
                        {
                            // copy the header
                            Array.Copy(_queueDataHeader, 0, buffer, bufferIndex, 6);

                            // queue the the header + chunk to be sent reliably (which gives the sequence to keep ordering of chunks)
                            BufferPacket(conn, new ArraySegment<byte>(buffer, bufferIndex, Constants.ChunkSize + 6), NetSendFlags.PriorityN1 | NetSendFlags.Reliable);
                            bufferIndex += Constants.ChunkSize;
                            needed -= Constants.ChunkSize;
                        }

                        // copy the header
                        Array.Copy(_queueDataHeader, 0, buffer, bufferIndex, 6);
                        BufferPacket(conn, new ArraySegment<byte>(buffer, bufferIndex, needed + 6), NetSendFlags.PriorityN1 | NetSendFlags.Reliable);

                        // check if we need more
                        if (sd.Offset >= sd.TotalLength)
                        {
                            // notify sender that this is the end
                            sd.RequestData(sd.Offset, null, 0, 0);
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

        private void ClearBuffers(Player p)
        {
            if (p == null)
                return;

            if (!(p[_connKey] is ConnData conn))
                return;

            // first handle the regular outlist and presized outlist
            Monitor.Enter(conn.olmtx);

            for (int i = 0; i < conn.outlist.Length; i++)
            {
                LinkedList<SubspaceBuffer> outlist = conn.outlist[i];

                LinkedListNode<SubspaceBuffer> nextNode = null;
                for (LinkedListNode<SubspaceBuffer> node = outlist.First; node != null; node = nextNode)
                {
                    nextNode = node.Next;

                    SubspaceBuffer b = node.Value;
                    if (b.CallbackInvoker != null)
                    {
                        // this is ugly, but we have to release the outlist mutex
                        // during these callbacks, because the callback might need
                        // to acquire some mutexes of its own, and we want to avoid
                        // deadlock.
                        Monitor.Exit(conn.olmtx);
                        b.CallbackInvoker.Invoke(p, false);
                        Monitor.Enter(conn.olmtx);
                    }

                    outlist.Remove(node);
                    b.Dispose();
                }
            }


            foreach (ISizedSendData sd in conn.sizedsends)
            {
                sd.RequestData(0, null, 0, 0);
            }
            conn.sizedsends.Clear();

            Monitor.Exit(conn.olmtx);

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
        }

        // call with player status locked
        private void ProcessLagouts(Player p, DateTime now, LinkedList<Player> toKill, LinkedList<Player> toFree)
        {
            if (!(p[_connKey] is ConnData conn))
                return;

            // this is used for lagouts and also for timewait
            TimeSpan diff = now - conn.lastPkt;

            // process lagouts
            if (p.WhenLoggedIn == PlayerState.Uninitialized // acts as flag to prevent dups
                && p.Status < PlayerState.LeavingZone // don't kick them if they're already on the way out
                && ((diff.TotalMilliseconds/10) > _config.droptimeout || conn.hitmaxretries || conn.hitmaxoutlist) // these three are our kicking conditions, for now
                )
            {
                // manually create an unreliable chat packet because we won't have time to send it properly
                string reason;
                if (conn.hitmaxretries)
                    reason = "too many reliable retries";
                else if (conn.hitmaxoutlist)
                    reason = "too many outgoing packets";
                else
                    reason = "no data";

                string message = "You have been disconnected because of lag (" + reason + ").\0"; // it looks like asss sends a null terminated string

                using (SubspaceBuffer buf = _bufferPool.Get())
                {
                    buf.Bytes[0] = 0x07;
                    buf.Bytes[1] = 0x08;
                    buf.Bytes[2] = 0x00;
                    buf.Bytes[3] = 0x00;
                    buf.Bytes[4] = 0x00;
                    buf.NumBytes = 5 + Encoding.ASCII.GetBytes(message, 0, message.Length, buf.Bytes, 5);

                    lock (conn.olmtx)
                    {
                        SendRaw(conn, buf.Bytes, buf.NumBytes);
                    }

                    _logManager.LogM(LogLevel.Info, nameof(Network), "[{0}] [pid={1}] player kicked for {2}", p.Name, p.Id, reason);

                    toKill.AddLast(p);
                }
            }

            // process timewait state
            // status is locked (shared) in here
            if (p.Status == PlayerState.TimeWait)
            {
                // finally, send disconnection packet
                using(SubspaceBuffer buf = _bufferPool.Get())
                {
                    buf.Bytes[0] = 0x00;
                    buf.Bytes[1] = 0x07;

                    SendRaw(conn, buf.Bytes, 2);
                }

                // clear all our buffers
                lock (conn.bigmtx)
                {
                    EndSized(p, false);
                }

                ClearBuffers(p);

                // tell encryption to forget about him
                if (conn.enc != null)
                {
                    conn.enc.Void(p);
                    _broker.ReleaseInterface(ref conn.enc, conn.iEncryptName);
                }

                // log message
                _logManager.LogM(LogLevel.Info, nameof(Network), "[{0}] [pid={1}] disconnected", p.Name, p.Id);

                lock (_hashmtx)
                {
                    if (_clienthash.Remove(conn.sin) == false)
                        _logManager.LogM(LogLevel.Error, nameof(Network), "internal error: established connection not in hash table");
                }

                toFree.AddLast(p);
            }
        }

        private void SubmitRelStats(Player p)
        {
            if (p == null)
                return;

            if (_lagCollect == null)
                return;

            if (!(p[_connKey] is ConnData conn))
                return;

            ReliableLagData rld = new ReliableLagData();
            rld.reldups = conn.relDups;
            rld.c2sn = (uint)conn.c2sn;
            rld.retries = conn.retries;
            rld.s2cn = (uint)conn.s2cn;
            _lagCollect.RelStats(p, ref rld);
        }

        private void SendRaw(ConnData conn, ArraySegment<byte> data)
        {
            int len = data.Count;

            Player p = conn.p;

#if CFG_DUMP_RAW_PACKETS
            DumpPk(string.Format("SEND: {0} bytes to pid ", len, p.Id), data);
#endif

            if ((p != null) && (conn.enc != null))
                len = conn.enc.Encrypt(p, data);
            //else if((conn.cc != null) && (conn.cc.enc != null))
                //len = conn.cc.enc.e

            if (len == 0)
                return;

            conn.whichSock.SendTo(data.Array, data.Offset, len, SocketFlags.None, conn.sin);

            conn.bytesSent += (ulong)len;
            conn.pktSent++;
            _globalStats.bytesent += (ulong)len;
            _globalStats.pktsent++;
        }

        private void SendRaw(ConnData conn, byte[] data, int len)
        {
            Debug.Assert(len <= Constants.MaxPacket);

            Player p = conn.p;

#if CFG_DUMP_RAW_PACKETS
            DumpPk(string.Format("SEND: {0} bytes to pid ", len, p.Id), new ArraySegment<byte>(data, 0, len));
#endif

            if ((p != null) && (conn.enc != null))
                len = conn.enc.Encrypt(p, data, len);
            //else if((conn.cc != null) && (conn.cc.enc != null))
                //len = conn.cc.enc.e

            if (len == 0)
                return;

            conn.whichSock.SendTo(data, len, SocketFlags.None, conn.sin);

            conn.bytesSent += (ulong)len;
            conn.pktSent++;
            _globalStats.bytesent += (ulong)len;
            _globalStats.pktsent++;
        }

        private void SendOutgoing(ConnData conn)
        {
            DateTime now = DateTime.UtcNow;

            // use an estimate of the average round-trip time to figure out when to resend a packet
            uint timeout = (uint)(conn.avgrtt + (4 * conn.rttdev));

            int canSend = conn.bw.GetCanBufferPackets();

            Clip(ref timeout, 250, 2000);

            // update the bandwidth limiter's counters
            conn.bw.Iter(now);

            // find smallest seqnum remaining in outlist
            int minseqnum = int.MaxValue;
            LinkedList<SubspaceBuffer> outlist = conn.outlist[(int)BandwidthPriorities.Reliable];
            foreach (SubspaceBuffer buffer in outlist)
            {
                ReliablePacket rp = new ReliablePacket(buffer.Bytes);
                int seqnum = rp.SeqNum;
                if (seqnum < minseqnum)
                    minseqnum = seqnum;
            }

            int retries = 0;
            int outlistlen = 0;

            using (SubspaceBuffer gpb = _bufferPool.Get())
            {
                GroupedPacket gp = new GroupedPacket(this, gpb.Bytes);

                // process highest priority first
                for (int pri = (int)BandwidthPriorities.NumPriorities - 1; pri >= 0; pri--)
                {
                    outlist = conn.outlist[pri];

                    LinkedListNode<SubspaceBuffer> nextNode = null;
                    for (LinkedListNode<SubspaceBuffer> node = outlist.First; node != null; node = nextNode)
                    {
                        nextNode = node.Next;

                        SubspaceBuffer buf = node.Value;
                        outlistlen++;

                        // check some invariants
                        ReliablePacket rp = new ReliablePacket(buf.Bytes);
                        byte t1 = rp.T1;
                        byte t2 = rp.T2;

                        if (t1 == 0x00 && t2 == 0x03)
                            Debug.Assert(pri == (int)BandwidthPriorities.Reliable);
                        else if (t1 == 0x00 && t2 == 0x04)
                            Debug.Assert(pri == (int)BandwidthPriorities.Ack);
                        else
                            Debug.Assert((pri != (int)BandwidthPriorities.Reliable) && (pri != (int)BandwidthPriorities.Ack));

                        // check if it's time to send this yet (use linearly increasing timeouts)
                        if ((buf.Tries != 0) && ((now - buf.LastRetry).TotalMilliseconds <= (timeout * buf.Tries)))
                            continue;

                        // only buffer fixed number of rel packets to client
                        int seqNum = rp.SeqNum;
                        if ((pri == (int)BandwidthPriorities.Reliable) && ((seqNum - minseqnum) > canSend))
                            continue;

                        // if we've retried too many times, kick the player
                        if (buf.Tries > _config.maxretries)
                        {
                            conn.hitmaxretries = true;
                            return;
                        }

                        // at this point, there's only one more check to determine if we're sending this packet now: bandwidth limiting.
                        if (!conn.bw.Check(
                            buf.NumBytes + ((buf.NumBytes <= 255) ? 1 : _config.overhead),
                            pri))
                        {
                            // try dropping it, if we can
                            if ((buf.Flags & NetSendFlags.Dropabble) != 0)
                            {
                                Debug.Assert(pri < (int)BandwidthPriorities.Reliable);
                                outlist.Remove(node);
                                buf.Dispose();
                                conn.pktdropped++;
                                outlistlen--;
                            }

                            //but in either case, skip it
                            continue;
                        }

                        if (buf.Tries > 0)
                        {
                            // this is a retry, not an initial send. record it for
                            // lag stats and also reduce bw limit (with clipping)
                            retries++;
                            conn.bw.AdjustForRetry();
                        }

                        buf.LastRetry = DateTime.UtcNow;
                        buf.Tries++;

                        // this sends it or adds it to a pending grouped packet
                        gp.Send(buf, conn);

                        // if we just sent an unreliable packet, free it so we don't send it again
                        if (pri != (int)BandwidthPriorities.Reliable)
                        {
                            outlist.Remove(node);
                            buf.Dispose();
                            outlistlen--;
                        }
                    }
                }

                // flush the pending grouped packet
                gp.Flush(conn);
            }

            conn.retries += (uint)retries;

            if (outlistlen > _config.maxoutlist)
                conn.hitmaxoutlist = true;
        }

        private void Clip(ref uint timeout, uint low, uint high)
        {
            if(timeout > high)
                timeout = high;
            else if(timeout < low)
                timeout = low;
        }

        private static bool IsOurs(Player p)
        {
            return p.Type == ClientType.Continuum || p.Type == ClientType.VIE;
        }

        private void HandleGamePacketRecieved(ListenData ld)
        {
            SubspaceBuffer buffer = _bufferPool.Get();
            EndPoint recievedFrom = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                buffer.NumBytes = ld.GameSocket.ReceiveFrom(buffer.Bytes, buffer.Bytes.Length, SocketFlags.None, ref recievedFrom);
            }
            catch (SocketException ex)
            {
                _logManager.LogM(LogLevel.Error, nameof(Network),
                    $"Caught a SocketException when calling ReceiveFrom. Error code: {ex.ErrorCode}. {ex}");
                buffer.Dispose();
                return;
            }

            if (buffer.NumBytes <= 0)
            {
                buffer.Dispose();
                return;
            }

#if CFG_DUMP_RAW_PACKETS
            DumpPk(string.Format("RECV: {0} bytes", buffer.NumBytes), new ArraySegment<byte>(buffer.Bytes, 0, buffer.NumBytes));
#endif

            if (!(recievedFrom is IPEndPoint remoteEndPoint))
            {
                buffer.Dispose();
                return;
            }

            if (_clienthash.TryGetValue(remoteEndPoint, out Player p) == false)
            {
                // this might be a new connection. make sure it's really a connection init packet
                if (IsConnectionInitPacket(buffer.Bytes))
                {
                    ConnectionInitCallback.Fire(_broker, remoteEndPoint, buffer.Bytes, buffer.NumBytes, ld);
                }
#if CFG_LOG_STUPID_STUFF
                else if (buffer.NumBytes > 1)
                {
                    _logManager.LogM(LogLevel.Drivel, nameof(Network), "recvd data ({0:X2} {1:X2} ; {2} bytes) before connection established",
                        buffer.Bytes[0],
                        buffer.Bytes[1],
                        buffer.NumBytes);
                }
                else
                {
                    _logManager.LogM(LogLevel.Drivel, nameof(Network), "recvd data ({0:X2} ; {1} bytes) before connection established",
                        buffer.Bytes[0],
                        buffer.NumBytes);
                }
#endif
                buffer.Dispose();
                return;
            }

            if (buffer.NumBytes > Constants.MaxPacket)
            {
                buffer.NumBytes = Constants.MaxPacket;
            }

            ConnData conn = p[_connKey] as ConnData;

            if (IsConnectionInitPacket(buffer.Bytes))
            {
                // here, we have a connection init, but it's from a
		        // player we've seen before. there are a few scenarios:
                if (p.Status == PlayerState.Connected)
                {
                    // if the player is in PlayerState.Connected, it means that
                    // the connection init response got dropped on the
                    // way to the client. we have to resend it.
                    ConnectionInitCallback.Fire(_broker, remoteEndPoint, buffer.Bytes, buffer.NumBytes, ld);
                }
                else
                {
                    /* otherwise, he probably just lagged off or his
                     * client crashed. ideally, we'd postpone this
                     * packet, initiate a logout procedure, and then
                     * process it. we can't do that right now, so drop
                     * the packet, initiate the logout, and hope that
                     * the client re-sends it soon. */
                    _playerData.KickPlayer(p);
                }

                buffer.Dispose();
                return;
            }

            // we shouldn't get packets in this state, but it's harmless if we do
            if (p.Status >= PlayerState.LeavingZone || p.WhenLoggedIn >= PlayerState.LeavingZone)
            {
                buffer.Dispose();
                return;
            }

            if (p.Status > PlayerState.TimeWait)
            {
                _logManager.LogM(LogLevel.Warn, nameof(Network), "[pid={0}] packet recieved from bad state {1}", p.Id, p.Status);

                // don't set lastpkt time here

                buffer.Dispose();
                return;
            }

            buffer.Conn = conn;
            conn.lastPkt = DateTime.UtcNow;
            conn.bytesRecieved += (ulong)buffer.NumBytes;
            conn.pktRecieved++;
            _globalStats.byterecvd += (ulong)buffer.NumBytes;
            _globalStats.pktrecvd++;

            IEncrypt enc = conn.enc;
            if (enc != null)
            {
                buffer.NumBytes = enc.Decrypt(p, buffer.Bytes, buffer.NumBytes);
            }

            if (buffer.NumBytes == 0)
            {
                // bad crc, or something
                _logManager.LogM(LogLevel.Malicious, nameof(Network), "[pid={0}] failure decrypting packet", p.Id);
                buffer.Dispose();
                return;
            }

#if CFG_DUMP_RAW_PACKETS
            DumpPk(string.Format("RECV: about to process {0} bytes", buffer.NumBytes), new ArraySegment<byte>(buffer.Bytes, 0, buffer.NumBytes));
#endif

            ProcessBuffer(buffer);
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
                PacketDelegate handler = _handlers[packetType];

                if (handler != null)
                    handler(conn.p, bytes, len);
                else
                    _logManager.LogM(LogLevel.Drivel, nameof(Network), "no handler for packet type [0x{0:X2}]", packetType);
            }
            else if (conn.cc != null)
            {
                // client connection
                conn.cc.i.HandlePacket(bytes, len);
            }
            else
            {
                _logManager.LogM(LogLevel.Drivel, nameof(Network), "no player or client connection, but got packet type [0x{0:X2}] of length {1}", packetType, len);
            }
        }

        private void MainloopWork_CallPacketHandlers(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            ConnData conn = buffer.Conn;
            if (conn == null)
                return;

            try
            {
                CallPacketHandlers(conn, buffer.Bytes, buffer.NumBytes);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        /// <summary>
        /// unreliable packets will be processed before the call returns and freed.
        /// network packets will be processed by the appropriate network handler,
        /// which may free it or not.
        /// </summary>
        /// <param name="buffer"></param>
        private void ProcessBuffer(SubspaceBuffer buffer)
        {
            ConnData conn = buffer.Conn;
            ReliablePacket rp = new ReliablePacket(buffer.Bytes);

            if (rp.T1 == 0x00)
            {
                // 'core' packet
                if ((rp.T2 < _oohandlers.Length) && (_oohandlers[rp.T2] != null))
                {
                    _oohandlers[rp.T2](buffer);
                }
                else
                {
                    if (conn.p != null)
                    {
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), "[{0}] [pid={1}] unknown network subtype {2}", conn.p.Name, conn.p.Id, rp.T2);
                    }
                    else
                    {
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), "(client connection) unknown network subtype {0}", rp.T2);
                    }
                    buffer.Dispose();
                }
            }
            else if (rp.T1 < MAXTYPES)
            {
                _mainloop.QueueMainWorkItem(MainloopWork_CallPacketHandlers, buffer);
            }
            else
            {
                try
                {
                    if (conn.p != null)
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), "[{0}] [pid={1}] unknown packet type {2}", conn.p.Name, conn.p.Id, rp.T1);
                    else
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), "(client connection) unknown packet type {0}", rp.T1);
                }
                finally
                {
                    buffer.Dispose();
                }
            }
        }

        private static bool IsConnectionInitPacket(byte[] data)
        {
            if (data == null)
                return false;

            ReliablePacket rp = new ReliablePacket(data);
            return (rp.T1 == 0x00) && ((rp.T2 == 0x01) || (rp.T2 == 0x11));
        }

        private void HandlePingPacketRecieved(ListenData ld)
        {
            if (ld == null)
                return;

            using (SubspaceBuffer buffer = _bufferPool.Get())
            {
                Socket s = ld.PingSocket;
                EndPoint recievedFrom = new IPEndPoint(IPAddress.Any, 0);
                buffer.NumBytes = s.ReceiveFrom(buffer.Bytes, 4, SocketFlags.None, ref recievedFrom);

                if (!(recievedFrom is IPEndPoint remoteEndPoint))
                    return;

                if (buffer.NumBytes <= 0)
                    return;

                //
                // Refresh data (if needed)
                //

                if (_pingData.LastRefresh == null 
                    || (DateTime.UtcNow - _pingData.LastRefresh) > _config.PingRefreshThreshold)
                {
                    foreach (ListenData listenData in _listenDataList)
                    {
                        listenData.PlayersTotal = listenData.PlayersPlaying = 0;
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
                            _pingData.GlobalTotal = (uint)total;
                            _pingData.GlobalPlaying = (uint)playing;

                            // arenas that are associated with ListenData
                            foreach (Arena arena in aman.ArenaList)
                            {
                                if (_listenConnectAsLookup.TryGetValue(arena.BaseName, out ListenData listenData))
                                {
                                    listenData.PlayersTotal += arena.Total;
                                    listenData.PlayersPlaying += arena.Playing;
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
                }

                //
                // Respond
                //

                if (buffer.NumBytes == 4)
                {
                    // bytes from recieve
                    buffer.Bytes[4] = buffer.Bytes[0];
                    buffer.Bytes[5] = buffer.Bytes[1];
                    buffer.Bytes[6] = buffer.Bytes[2];
                    buffer.Bytes[7] = buffer.Bytes[3];

                    Span<byte> span = new Span<byte>(buffer.Bytes, 0, 4);

                    if (string.IsNullOrWhiteSpace(ld.ConnectAs))
                    {
                        // global
                        BitConverter.TryWriteBytes(span, _pingData.GlobalTotal);
                    }
                    else
                    {
                        // specific arena/zone
                        BitConverter.TryWriteBytes(span, ld.PlayersTotal);
                    }

                    int bytesSent = s.SendTo(buffer.Bytes, 8, SocketFlags.None, remoteEndPoint);
                }
                else if (buffer.NumBytes == 8)
                {
                    // TODO: add the ability handle ASSS' extended ping packets
                }
            }

            _globalStats.pcountpings++;
        }

        private void HandleClientPacketRecieved()
        {
            SubspaceBuffer buffer = _bufferPool.Get();

            EndPoint recievedFrom = new IPEndPoint(IPAddress.Any, 0);
            buffer.NumBytes = _clientSocket.ReceiveFrom(buffer.Bytes, buffer.Bytes.Length, SocketFlags.None, ref recievedFrom);

            if (buffer.NumBytes < 1)
            {
                buffer.Dispose();
                return;
            }

#if CFG_DUMP_RAW_PACKETS
            DumpPk(string.Format("RAW CLIENT DATA: {0} bytes", buffer.NumBytes), new ArraySegment<byte>(buffer.Bytes, 0, buffer.NumBytes));
#endif

            // TODO
        }

        private SubspaceBuffer BufferPacket(ConnData conn, byte[] data, int len, NetSendFlags flags, IReliableCallbackInvoker callbackInvoker = null)
        {
            return BufferPacket(conn, new ArraySegment<byte>(data, 0, len), flags, callbackInvoker);
        }

        private SubspaceBuffer BufferPacket(ConnData conn, ArraySegment<byte> data, NetSendFlags flags, IReliableCallbackInvoker callbackInvoker = null)
        {
            int len = data.Count;

            // data has to be able to fit into a reliable packet
            Debug.Assert(len <= Constants.MaxPacket - Constants.ReliableHeaderLen);

            // you can't buffer already-reliable packets
            Debug.Assert(!(data.Array[0] == 0x00 && data.Array[1] == 0x03));

            // reliable packets can't be droppable
            Debug.Assert((flags & (NetSendFlags.Reliable | NetSendFlags.Dropabble)) != (NetSendFlags.Reliable | NetSendFlags.Dropabble));

            BandwidthPriorities pri;
            if ((flags & NetSendFlags.PriorityN1) == NetSendFlags.PriorityN1)
            {
                pri = BandwidthPriorities.UnreliableLow;
            }
            else if (((flags & NetSendFlags.PriorityP4) == NetSendFlags.PriorityP4) ||
                ((flags & NetSendFlags.PriorityP5) == NetSendFlags.PriorityP5))
            {
                pri = BandwidthPriorities.UnreliableHigh;
            }
            else
            {
                pri = BandwidthPriorities.Unreliable;
            }

            if ((flags & NetSendFlags.Reliable) == NetSendFlags.Reliable)
                pri = BandwidthPriorities.Reliable;

            if ((flags & NetSendFlags.Ack) == NetSendFlags.Ack)
                pri = BandwidthPriorities.Ack;

            // update global stats based on requested priority
            _globalStats.pri_stats[(int)pri] += (ulong)len;

            // try the fast path
            if (((flags & NetSendFlags.Reliable) != NetSendFlags.Reliable) &&
                ((flags & NetSendFlags.Urgent) == NetSendFlags.Urgent))
            {
                // urgent and not reliable
                if (conn.bw.Check(len, (int)pri))
                {
                    SendRaw(conn, data);
                    return null;
                }
                else
                {
                    if ((flags & NetSendFlags.Dropabble) == NetSendFlags.Dropabble)
                    {
                        conn.pktdropped++;
                        return null;
                    }
                }
            }

            SubspaceBuffer buf = _bufferPool.Get();
            buf.Conn = conn;
            buf.LastRetry = DateTime.UtcNow.Subtract(new TimeSpan(0, 0, 100));
            buf.Tries = 0;
            buf.CallbackInvoker = callbackInvoker;
            buf.Flags = flags;

            // get data into packet
            if ((flags & NetSendFlags.Reliable) == NetSendFlags.Reliable)
            {
                buf.NumBytes = len + Constants.ReliableHeaderLen;
                ReliablePacket rp = new ReliablePacket(buf.Bytes);
                rp.T1 = 0x00;
                rp.T2 = 0x03;
                rp.SeqNum = conn.s2cn++;
                rp.SetData(data);
            }
            else
            {
                buf.NumBytes = len;
                Array.Copy(data.Array, data.Offset, buf.Bytes, 0, data.Count);
            }

            conn.outlist[(int)pri].AddLast(buf);

            return buf;
        }

        #region oohandlers (network layer header handling)

        private void ProcessKeyResponse(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                if (buffer.NumBytes != 6)
                    return;

                ConnData conn = buffer.Conn;
                if (conn == null)
                    return;

                if (conn.cc != null)
                    conn.cc.i.Connected();
                else if (conn.p != null)
                    _logManager.LogP(LogLevel.Malicious, nameof(Network), conn.p, "got key response packet");
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private void ProcessReliable(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            if (buffer.NumBytes < 7)
            {
                buffer.Dispose();
                return;
            }

            ReliablePacket rp = new ReliablePacket(buffer.Bytes);
            int sn = rp.SeqNum;

            ConnData conn = buffer.Conn;
            if (conn == null)
            {
                buffer.Dispose();
                return;
            }

            Monitor.Enter(conn.relmtx);

            if ((sn - conn.c2sn) >= Constants.CFG_INCOMING_BUFFER || sn < 0)
            {
                Monitor.Exit(conn.relmtx);

                // just drop it
                if (conn.p != null)
                    _logManager.LogM(LogLevel.Drivel, nameof(Network), "[{0}] [pid={1}] reliable packet with too big delta ({2} - {3})", conn.p.Name, conn.p.Id, sn, conn.c2sn);
                else
                    _logManager.LogM(LogLevel.Drivel, nameof(Network), "(client connection) reliable packet with too big delta ({0} - {1})", sn, conn.c2sn);

                buffer.Dispose();
            }
            else
            {
                // store it and ack

                // add to rel stuff to be processed
                int spot = sn % Constants.CFG_INCOMING_BUFFER;
                if ((sn < conn.c2sn) || (conn.relbuf[spot] != null))
                {
                    // a dup
                    conn.relDups++;
                    buffer.Dispose();
                }
                else
                {
                    conn.relbuf[spot] = buffer;
                }

                Monitor.Exit(conn.relmtx);

                // send the ack
                using (SubspaceBuffer ackBuffer = _bufferPool.Get())
                {
                    AckPacket ap = new AckPacket(ackBuffer.Bytes);
                    ap.T1 = 0x00;
                    ap.T2 = 0x04;
                    ap.SeqNum = sn;

                    lock (conn.olmtx)
                    {
                        BufferPacket(conn, ackBuffer.Bytes, AckPacket.Length, NetSendFlags.Ack);
                    }
                }

                // add to global rel list for processing
                _relqueue.Enqueue(conn);
            }
        }

        private void ProcessAck(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            if (buffer.NumBytes != 6)
            {
                // ack packets are 6 bytes long
                buffer.Dispose();
                return;
            }

            ConnData conn = buffer.Conn;
            if (conn == null)
            {
                buffer.Dispose();
                return;
            }

            ReliablePacket rp = new ReliablePacket(buffer.Bytes);
            int seqNum = rp.SeqNum;

            Monitor.Enter(conn.olmtx);

            LinkedList<SubspaceBuffer> outlist = conn.outlist[(int)BandwidthPriorities.Reliable];
            LinkedListNode<SubspaceBuffer> nextNode = null;
            for (LinkedListNode<SubspaceBuffer> node = outlist.First; node != null; node = nextNode)
            {
                nextNode = node.Next;

                SubspaceBuffer b = node.Value;
                ReliablePacket brp = new ReliablePacket(b.Bytes);
                if (seqNum == brp.SeqNum)
                {
                    outlist.Remove(node);
                    Monitor.Exit(conn.olmtx);

                    b.CallbackInvoker?.Invoke(conn.p, true);

                    if (b.Tries == 1)
                    {
                        int rtt = (int)DateTime.UtcNow.Subtract(b.LastRetry).TotalMilliseconds;
                        if (rtt < 0)
                        {
                            _logManager.LogM(LogLevel.Error, nameof(Network), "negative rtt ({0}); clock going backwards", rtt);
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

                    // handle limit adjustment
                    conn.bw.AdjustForAck();

                    b.Dispose();
                    buffer.Dispose();
                    return;
                }
            }

            Monitor.Exit(conn.olmtx);
            buffer.Dispose();
        }

        private void ProcessSyncRequest(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                if (buffer.NumBytes != 14)
                    return;

                ConnData conn = buffer.Conn;
                if (conn == null)
                    return;

                TimeSyncC2SPacket cts = new TimeSyncC2SPacket(buffer.Bytes);
                using (SubspaceBuffer b = _bufferPool.Get())
                {
                    uint clientTime = cts.Time;
                    uint serverTime = ServerTick.Now;

                    TimeSyncS2CPacket ts = new TimeSyncS2CPacket(b.Bytes);
                    ts.Initialize(clientTime, serverTime);

                    lock (conn.olmtx)
                    {
                        // note: this bypasses bandwidth limits
                        SendRaw(conn, b.Bytes, TimeSyncS2CPacket.Length);

                        // submit data to lagdata
                        if (_lagCollect != null && conn.p != null)
	                    {
		                    TimeSyncData data;
		                    data.s_pktrcvd = conn.pktRecieved;
		                    data.s_pktsent = conn.pktSent;
		                    data.c_pktrcvd = cts.PktRecvd;
		                    data.c_pktsent = cts.PktSent;
		                    data.s_time = serverTime;
                            data.c_time = clientTime;
                            _lagCollect.TimeSync(conn.p, ref data);
	                    }
                    }
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private void ProcessDrop(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                if (buffer.NumBytes != 2)
                    return;

                ConnData conn = buffer.Conn;
                if (conn == null)
                    return;

                if (conn.p != null)
                {
                    _playerData.KickPlayer(conn.p);
                }
                else if (conn.cc != null)
                {
                    //buf->conn->cc->i->Disconnected();
                    /* FIXME: this sends an extra 0007 to the client. that should
                        * probably go away. */
                    //DropClientConnection(buf->conn->cc);
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private struct BigPacketWork
        {
            public ConnData ConnData;
            public byte[] PacketBytes;
            public int PacketLength;
        }

        private void MainloopWork_CallBigPacketHandlers(BigPacketWork work)
        {
            CallPacketHandlers(work.ConnData, work.PacketBytes, work.PacketLength);
        }

        private void ProcessBigData(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                if (buffer.NumBytes < 3) // 0x00, [0x08 or 0x09], and then at least one byte of data
                    return;

                ConnData conn = buffer.Conn;
                if (conn == null)
                    return;

                lock (conn.bigmtx)
                {
                    int newsize = conn.bigrecv.size + buffer.NumBytes - 2;
                    if (newsize <= 0 || newsize > Constants.MaxBigPacket)
                    {
                        if (conn.p != null)
                            _logManager.LogP(LogLevel.Malicious, nameof(Network), conn.p, "refusing to allocate {0} bytes (> {1})", newsize, Constants.MaxBigPacket);
                        else
                            _logManager.LogM(LogLevel.Malicious, nameof(Network), "(client connection) refusing to allocate {0} bytes (> {1})", newsize, Constants.MaxBigPacket);

                        conn.bigrecv.Free();
                        return;
                    }

                    byte[] newbuf = null;

                    if (conn.bigrecv.room < newsize)
                    {
                        conn.bigrecv.room *= 2;
                        if (conn.bigrecv.room < newsize)
                            conn.bigrecv.room = newsize;

                        newbuf = new byte[conn.bigrecv.room];
                        Array.Copy(conn.bigrecv.buf, newbuf, conn.bigrecv.buf.Length);
                        conn.bigrecv.buf = newbuf;
                    }
                    else
                        newbuf = conn.bigrecv.buf;

                    if (newbuf == null)
                    {
                        if (conn.p != null)
                            _logManager.LogP(LogLevel.Error, nameof(Network), conn.p, "cannot allocate {0} bytes for bigpacket", newsize);
                        else
                            _logManager.LogM(LogLevel.Error, nameof(Network), "(client connection) cannot allocate {0} bytes for bigpacket", newsize);

                        conn.bigrecv.Free();
                        return;
                    }

                    Array.Copy(buffer.Bytes, 2, newbuf, conn.bigrecv.size, buffer.NumBytes - 2);
                    conn.bigrecv.buf = newbuf;
                    conn.bigrecv.size = newsize;

                    ReliablePacket rp = new ReliablePacket(buffer.Bytes);
                    if (rp.T2 == 0x08)
                        return;

                    // Getting here means the we got 0x09 (end of "Big" data packet), so we should process it now.
                    if (newbuf[0] > 0 && newbuf[0] < MAXTYPES)
                    {
                        _mainloop.QueueMainWorkItem(
                            MainloopWork_CallBigPacketHandlers,
                            new BigPacketWork()
                            {
                                ConnData = conn,
                                PacketBytes = newbuf,
                                PacketLength = newsize,
                            });
                    }
                    else
                    {
                        if (conn.p != null)
                            _logManager.LogP(LogLevel.Warn, nameof(Network), conn.p, "bad type for bigpacket: {0}", newbuf[0]);
                        else
                            _logManager.LogM(LogLevel.Warn, nameof(Network), "(client connection) bad type for bigpacket: {0}", newbuf[0]);
                    }

                    conn.bigrecv.Free();
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private void ProcessPresize(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                if (buffer.NumBytes < 7)
                    return;

                ConnData conn = buffer.Conn;
                if (conn == null)
                    return;

                ReliablePacket rp = new ReliablePacket(buffer.Bytes);
                int size = rp.SeqNum;
                ArraySegment<byte> rpData = rp.GetData(buffer.NumBytes - 6);

                lock (conn.bigmtx)
                {
                    // only handle presized packets for player connections, not client connections
                    if (conn.p == null)
                        return;

                    if (conn.sizedrecv.offset == 0)
                    {
                        // first packet

                        int type = rpData.Array[rpData.Offset];
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
                        _logManager.LogP(LogLevel.Malicious, nameof(Network), conn.p, "length mismatch in sized packet");
                        EndSized(conn.p, false);
                    }
                    else if ((conn.sizedrecv.offset + buffer.NumBytes - 6) > size)
                    {
                        _logManager.LogP(LogLevel.Malicious, nameof(Network), conn.p, "sized packet overflow");
                        EndSized(conn.p, false);
                    }
                    else
                    {
                        _sizedhandlers[conn.sizedrecv.type]?.Invoke(conn.p, rpData, conn.sizedrecv.offset, size);

                        conn.sizedrecv.offset += rpData.Count;

                        if (conn.sizedrecv.offset >= size)
                            EndSized(conn.p, true); // sized recieve is complete
                    }
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private void ProcessCancelReq(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                if (buffer.NumBytes != 2)
                    return;

                ConnData conn = buffer.Conn;
                if (conn == null)
                    return;

                // the client has requested a cancel for the file transfer
                lock (conn.olmtx)
                {
                    // cancel current presized transfer
                    LinkedListNode<ISizedSendData> node = conn.sizedsends.First;
                    if (node != null)
                    {
                        ISizedSendData sd = node.Value;
                        if (sd != null)
                        {
                            sd.RequestData(0, null, 0, 0); // notify transfer complete
                        }
                        conn.sizedsends.RemoveFirst();
                    }

                    ReliablePacket rp = new ReliablePacket(buffer.Bytes); // reusing buffer
                    rp.T1 = 0x00;
                    rp.T2 = 0x0C;
                    BufferPacket(conn, buffer.Bytes, 2, NetSendFlags.Reliable);
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private void ProcessCancel(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                if (buffer.NumBytes != 2)
                    return;

                ConnData conn = buffer.Conn;
                if (conn == null)
                    return;

                if (conn.p != null)
                {
                    lock (conn.bigmtx)
                    {
                        EndSized(conn.p, false);
                    }
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        // call with bigmtx locked
        private void EndSized(Player p, bool success)
        {
            if (p == null)
                return;

            if (!(p[_connKey] is ConnData conn))
                return;

            if (conn.sizedrecv.offset != 0)
            {
                int type = conn.sizedrecv.type;
                int arg = success ? conn.sizedrecv.totallen : -1;

                // tell listeners that they're cancelled
                if (type < MAXTYPES)
                {
                    _sizedhandlers[type]?.Invoke(p, null, arg, arg);
                }

                conn.sizedrecv.type = 0;
                conn.sizedrecv.totallen = 0;
                conn.sizedrecv.offset = 0;
            }
        }

        private void ProcessGrouped(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                if (buffer.NumBytes < 4)
                    return;

                ConnData conn = buffer.Conn;
                if (conn == null)
                    return;

                int pos = 2, len = 1;

                while (pos < buffer.NumBytes && len > 0)
                {
                    len = buffer.Bytes[pos++];
                    if (pos + len <= buffer.NumBytes)
                    {
                        SubspaceBuffer b = _bufferPool.Get();
                        b.Conn = conn;
                        b.NumBytes = len;
                        Array.Copy(buffer.Bytes, pos, b.Bytes, 0, len);
                        ProcessBuffer(b);
                    }

                    pos += len;
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private void ProcessSpecial(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                Player p = buffer.Conn?.p;
                if (p == null)
                    return;

                ReliablePacket rp = new ReliablePacket(buffer.Bytes);
                _nethandlers[rp.T2]?.Invoke(p, buffer.Bytes, buffer.NumBytes);
            }
            finally
            {
                buffer.Dispose();
            }
        }

        #endregion

        #region IModule Members

        public bool Load(
            ComponentBroker broker,
            IPlayerData playerData,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloop mainloop,
            IMainloopTimer mainloopTimer,
            IBandwidthLimit bandwidthLimit,
            ILagCollect lagCollect)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopTimer = mainloopTimer ?? throw new ArgumentNullException(nameof(mainloopTimer));
            _bandwithLimit = bandwidthLimit ?? throw new ArgumentNullException(nameof(bandwidthLimit));
            _lagCollect = lagCollect ?? throw new ArgumentNullException(nameof(lagCollect));

            _connKey = _playerData.AllocatePlayerData<ConnData>();

            _config.droptimeout = _configManager.GetInt(_configManager.Global, "Net", "DropTimeout", 3000); // centiseconds (ticks)
            _config.maxoutlist = _configManager.GetInt(_configManager.Global, "Net", "MaxOutlistSize", 500);

            // (deliberately) undocumented settings
            _config.maxretries = _configManager.GetInt(_configManager.Global, "Net", "MaxRetries", 15);
            _config.queue_threshold = _configManager.GetInt(_configManager.Global, "Net", "PresizedQueueThreshold", 5);
            _config.queue_packets = _configManager.GetInt(_configManager.Global, "Net", "PresizedQueuePackets", 25);
            int reliableThreadCount = _configManager.GetInt(_configManager.Global, "Net", "ReliableThreads", 1);
            _config.overhead = _configManager.GetInt(_configManager.Global, "Net", "PerPacketOverhead", 28);
            _config.PingRefreshThreshold = TimeSpan.FromMilliseconds(10 * _configManager.GetInt(_configManager.Global, "Net", "PingDataRefreshTime", 200));
            _config.simplepingpopulationmode = (PingPopulationMode)_configManager.GetInt(_configManager.Global, "Net", "SimplePingPopulationMode", 1);

            if (InitializeSockets() == false)
                return false;

            _stopToken = _stopCancellationTokenSource.Token;

            // recieve thread
            Thread thread = new Thread(ReceiveThread);
            thread.Name = "network-recv";
            thread.Start();
            _threadList.Add(thread);

            // send thread
            thread = new Thread(SendThread);
            thread.Name = "network-send";
            thread.Start();
            _threadList.Add(thread);

            // reliable threads
            for (int i = 0; i < reliableThreadCount; i++)
            {
                thread = new Thread(RelThread);
                thread.Name = "network-rel-" + i;
                thread.Start();
                _reliableThreads.Add(thread);
                _threadList.Add(thread);
            }

            // queue more data thread (sends sized data)
            _mainloopTimer.SetTimer(QueueMoreData, 200, 110, null); // TODO: maybe change it to be in its own thread?

            _iNetworkToken = broker.RegisterInterface<INetwork>(this);
            _iNetworkClientToken = broker.RegisterInterface<INetworkClient>(this);
            _iNetworkEncryptionToken = broker.RegisterInterface<INetworkEncryption>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            if (broker.UnregisterInterface<INetwork>(ref _iNetworkToken) != 0)
                return false;

            if (broker.UnregisterInterface<INetworkClient>(ref _iNetworkClientToken) != 0)
                return false;

            if (broker.UnregisterInterface<INetworkEncryption>(ref _iNetworkEncryptionToken) != 0)
                return false;

            _mainloopTimer.ClearTimer(QueueMoreData, null);

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
            _listenConnectAsLookup.Clear();

            _clientSocket.Close();
            _clientSocket = null;

            _playerData.FreePlayerData(_connKey);
            
            return true;
        }

        #endregion

        #region IModuleLoaderAware Members

        bool IModuleLoaderAware.PostLoad(ComponentBroker broker)
        {
            // NOOP
            return true;
        }

        bool IModuleLoaderAware.PreUnload(ComponentBroker broker)
        {
            //
            // Disconnect all clients nicely
            //

            _playerData.Lock();

            try
            {
                byte[] disconnectPacket = new byte[] { 0x00, 0x07 };

                foreach (Player player in _playerData.PlayerList)
                {
                    if (IsOurs(player))
                    {
                        if (!(player[_connKey] is ConnData conn))
                            continue;

                        SendRaw(conn, disconnectPacket, disconnectPacket.Length);

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
            
            // TODO: 

            return true;
        }

        #endregion

        #region INetworkEncryption Members

        void INetworkEncryption.ReallyRawSend(IPEndPoint remoteEndpoint, byte[] pkt, int len, ListenData ld)
        {
            if (remoteEndpoint == null)
                throw new ArgumentNullException(nameof(remoteEndpoint));

            if (pkt == null)
                throw new ArgumentNullException(nameof(pkt));

            if (len < 1)
                throw new ArgumentOutOfRangeException(nameof(len), "Need at least 1 byte to send.");

            if (ld == null)
                throw new ArgumentNullException(nameof(ld));

#if CFG_DUMP_RAW_PACKETS
            DumpPk(string.Format("SENDRAW: {0} bytes", len), new ArraySegment<byte>(pkt, 0, len));
#endif

            ld.GameSocket.SendTo(pkt, len, SocketFlags.None, remoteEndpoint);

            _globalStats.bytesent += (ulong)len;
            _globalStats.pktsent++;
        }

        Player INetworkEncryption.NewConnection(ClientType clientType, IPEndPoint remoteEndpoint, string iEncryptName, ListenData ld)
        {
            if (ld == null)
                throw new ArgumentNullException(nameof(ld));

            // certain ports may have restrictions on client types
            if ((clientType == ClientType.VIE && !ld.AllowVIE) ||
                (clientType == ClientType.Continuum && !ld.AllowContinuum))
                return null;

            // try to find a matching player for the endpoint
            if (remoteEndpoint != null && _clienthash.TryGetValue(remoteEndpoint, out Player p))
            {
                /* we found it. if its status is S_CONNECTED, just return the
		            * pid. it means we have to redo part of the connection init. */

                if (p.Status <= PlayerState.Connected)
                {
                    return p;
                }
                else
                {
                    // otherwise, something is horribly wrong. make a note to this effect
                    _logManager.LogM(LogLevel.Error, nameof(Network), "[pid={0}] NewConnection called for an established address", p.Id);
                    return null;
                }
            }

            p = _playerData.NewPlayer(clientType);
            ConnData conn = p[_connKey] as ConnData;

            IEncrypt enc = null;
            if (iEncryptName != null)
            {
                enc = _broker.GetInterface<IEncrypt>(iEncryptName);

                if (enc == null)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), "[pid={0}] NewConnection called to use IEncrypt '{0}', but interface not found", p.Id, iEncryptName);
                    return null;
                }
            }
            
            conn.Initalize(enc, iEncryptName, _bandwithLimit.New());
            conn.p = p;

            // copy data from ListenData
            conn.whichSock = ld.GameSocket;
            p.ConnectAs = ld.ConnectAs;

            p.IpAddress = remoteEndpoint.Address;

            switch (clientType)
            {
                case ClientType.VIE:
                    p.ClientName = "<ss/vie client>";
                    break;
                case ClientType.Continuum:
                    p.ClientName = "<continuum>";
                    break;
                default:
                    p.ClientName = "<unknown game client>";
                    break;
            }

            if (remoteEndpoint != null)
            {
                conn.sin = remoteEndpoint;

                lock (_hashmtx)
                {
                    _clienthash[remoteEndpoint] = p;
                }
            }

            _playerData.WriteLock();
            try
            {
                p.Status = PlayerState.Connected;
            }
            finally
            {
                _playerData.WriteUnlock();
            }

            if (remoteEndpoint != null)
            {
                _logManager.LogM(LogLevel.Drivel, nameof(Network), "[pid={0}] new connection from {1}", p.Id, remoteEndpoint);
            }
            else
            {
                _logManager.LogM(LogLevel.Drivel, nameof(Network), "[pid={0}] new internal connection", p.Id);
            }

            return p;
        }

        #endregion

        #region INetwork Members

        void INetwork.SendToOne(Player p, byte[] data, int len, NetSendFlags flags)
        {
            if (p == null)
                return;

            if (data == null)
                return;

            if (len <= 0)
                return;

            if (!IsOurs(p))
                return;

            if (!(p[_connKey] is ConnData conn))
                return;

            // see if we can do it the quick way
            if (len <= (Constants.MaxPacket - Constants.ReliableHeaderLen))
            {
                lock (conn.olmtx)
                {
                    BufferPacket(conn, data, len, flags);
                }
            }
            else
            {
                // TODO: investigate a way to not allocate
                Player[] set = new Player[] { p };
                SendToSet(set, data, len, flags);
            }
        }

        void INetwork.SendToArena(Arena arena, Player except, byte[] data, int len, NetSendFlags flags)
        {
            if (data == null)
                return;

            LinkedList<Player> set = new LinkedList<Player>();

            _playerData.Lock();
            try
            {
                foreach (Player p in _playerData.PlayerList)
                {
                    if (p.Status == PlayerState.Playing &&
                        (p.Arena == arena || arena == null) &&
                        p != except &&
                        IsOurs(p))
                    {
                        set.AddLast(p);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            SendToSet(set, data, len, flags);
        }

        void INetwork.SendToSet(IEnumerable<Player> set, byte[] data, int len, NetSendFlags flags)
        {
            SendToSet(set, data, len, flags);
        }

        private void SendToSet(IEnumerable<Player> set, byte[] data, int len, NetSendFlags flags)
        {
            if (set == null)
                return;

            if (data == null)
                return;

            if (len <= 0)
                return;

            if (len > Constants.MaxPacket - Constants.ReliableHeaderLen)
            {
                // use 00 08/9 packets (big data packets)
                // send these reliably (to maintain ordering with sequence #)
                using (DataBuffer buffer = Pool<DataBuffer>.Default.Get())
                {
                    buffer.Bytes[0] = 0x00;
                    buffer.Bytes[1] = 0x08;

                    int position = 0;

                    // first send the 08 packets
                    while (len > Constants.ChunkSize)
                    {
                        Array.Copy(data, position, buffer.Bytes, 2, Constants.ChunkSize);
                        SendToSet(set, buffer.Bytes, Constants.ChunkSize + 2, flags); // only the 09 is sent reliably for some reason
                        position += Constants.ChunkSize;
                        len -= Constants.ChunkSize;
                    }

                    // final packet is the 09 (signals the end of the big data)
                    buffer.Bytes[1] = 0x09;
                    Array.Copy(data, position, buffer.Bytes, 2, len);
                    SendToSet(set, buffer.Bytes, len + 2, flags | NetSendFlags.Reliable);
                }
            }
            else
            {
                foreach (Player p in set)
                {
                    if (!(p[_connKey] is ConnData conn))
                        continue;

                    if (!IsOurs(p))
                        continue;

                    lock (conn.olmtx)
                    {
                        BufferPacket(conn, data, len, flags);
                    }
                }
            }
        }

        void INetwork.SendWithCallback(Player p, byte[] data, int len, ReliableDelegate callback)
        {
            SendWithCallback(p, data, len, new ReliableCallbackInvoker(callback));
        }

        void INetwork.SendWithCallback<T>(Player p, byte[] data, int len, ReliableDelegate<T> callback, T clos)
        {
            SendWithCallback(p, data, len, new ReliableCallbackInvoker<T>(callback, clos));
        }

        private void SendWithCallback(Player p, byte[] data, int len, IReliableCallbackInvoker callbackInvoker)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p));

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (len < 1)
                throw new ArgumentOutOfRangeException(nameof(len), "Must be >= 1");

            if (callbackInvoker == null)
                throw new ArgumentNullException(nameof(callbackInvoker));

            if (!(p[_connKey] is ConnData conn))
                return;

            // we can't handle big packets here
            Debug.Assert(len <= (Constants.MaxPacket - Constants.ReliableHeaderLen));

            if (!IsOurs(p))
                return;

            lock (conn.olmtx)
            {
                BufferPacket(conn, data, len, NetSendFlags.Reliable, callbackInvoker);
            }
        }

        void INetwork.SendToTarget(ITarget target, byte[] data, int len, NetSendFlags flags)
        {
            _playerData.TargetToSet(target, out LinkedList<Player> set);
            SendToSet(set, data, len, flags);
        }

        bool INetwork.SendSized<T>(Player p, T clos, int len, GetSizedSendDataDelegate<T> requestCallback)
        {
            if (!IsOurs(p))
            {
                _logManager.LogP(LogLevel.Drivel, nameof(Network), p, "tried to send sized data to non-udp client");
                return false;
            }

            if (!(p[_connKey] is ConnData conn))
                return false;

            SizedSendData<T> sd = new SizedSendData<T>(requestCallback, clos, len, 0);

            lock (conn.olmtx)
            {
                conn.sizedsends.AddLast(sd);
            }

            return true;
        }

        void INetwork.AddPacket(int pktype, PacketDelegate func)
        {
            if (pktype >= 0 && pktype < MAXTYPES)
            {
                PacketDelegate d = _handlers[pktype];
                _handlers[pktype] = (d == null) ? func : (d += func);
            }
            else if((pktype & 0xFF) == 0)
            {
                int b2 = pktype >> 8;

                if (b2 >= 0 && b2 < _nethandlers.Length && _nethandlers[b2] == null)
                {
                    _nethandlers[b2] = func;
                }
            }
        }

        void INetwork.RemovePacket(int pktype, PacketDelegate func)
        {
            if (pktype >= 0 && pktype < MAXTYPES)
            {
                PacketDelegate d = _handlers[pktype];
                if (d != null)
                {
                    _handlers[pktype] = (d -= func);
                }
            }
            else if ((pktype & 0xFF) == 0)
            {
                int b2 = pktype >> 8;

                if (b2 >= 0 && b2 < _nethandlers.Length && _nethandlers[b2] == func)
                {
                    _nethandlers[b2] = null;
                }
            }
        }

        void INetwork.AddSizedPacket(int pktype, SizedPacketDelegate func)
        {
            if (pktype >= 0 && pktype < MAXTYPES)
            {
                SizedPacketDelegate d = _sizedhandlers[pktype];
                _sizedhandlers[pktype] = (d == null) ? func : (d += func);
            }
        }

        void INetwork.RemoveSizedPacket(int pktype, SizedPacketDelegate func)
        {
            if (pktype >= 0 && pktype < MAXTYPES)
            {
                SizedPacketDelegate d = _sizedhandlers[pktype];
                if (d != null)
                {
                    _sizedhandlers[pktype] = (d -= func);
                }
            }
        }

        IReadOnlyNetStats INetwork.GetStats()
        {
            _globalStats.buffercount = Convert.ToUInt64(_bufferPool.ObjectsCreated);
            _globalStats.buffersused = _globalStats.buffercount - Convert.ToUInt64(_bufferPool.ObjectsAvailable);

            return _globalStats;
        }

        NetClientStats INetwork.GetClientStats(Player p)
        {
            if (!(p[_connKey] is ConnData conn))
                return null;

            NetClientStats stats = new NetClientStats();
            stats.s2cn = conn.s2cn;
            stats.c2sn = conn.c2sn;
            stats.PacketsSent = conn.pktSent;
            stats.PacketsReceived = conn.pktRecieved;
            stats.BytesSent = conn.bytesSent;
            stats.BytesReceived = conn.bytesRecieved;
            stats.PacketsDropped = conn.pktdropped;

            if (conn.enc != null)
            {
                stats.EncryptionName = conn.iEncryptName;
            }

            stats.IPEndPoint = conn.sin;

            //TODO: _bandwithLimit.

            return stats;
        }

        IReadOnlyList<ListenData> INetwork.Listening => _readOnlyListenData;

        #endregion

        #region INetworkClient Members

        BaseClientConnection INetworkClient.MakeClientConnection(string address, int port, IClientConn icc, IClientEncrypt ice)
        {
            // TODO: it looks like billing_ssc uses this
            return null;
        }

        void INetworkClient.SendPacket(BaseClientConnection cc, byte[] pkt, int len, int flags)
        {
            // TODO: it looks like billing_ssc uses this
        }

        void INetworkClient.DropConnection(BaseClientConnection cc)
        {
            // TODO: it looks like billing_ssc uses this
            if (cc is ClientConnection clientConnection)
            {
                DropConnection(clientConnection);
            }
        }

        #endregion

        private void DropConnection(ClientConnection cc)
        {
        }

#if CFG_DUMP_RAW_PACKETS
        private void DumpPk(string description, ArraySegment<byte> d)
        {
            int len = d.Count;
            StringBuilder sb = new StringBuilder(description.Length + (len * 3));
            sb.AppendLine(description);

            int dIdx = d.Offset;

            StringBuilder asciiBuilder = new StringBuilder(16);

            while (len > 0)
            {
                for (int c = 0; c < 16 && len > 0; c++, len--)
                {
                    if(c > 0)
                        sb.Append(' ');

                    asciiBuilder.Append(!char.IsControl((char)d.Array[dIdx]) ? (char)d.Array[dIdx] : '.');
                    sb.Append(d.Array[dIdx++].ToString("X2"));
                }

                sb.Append("  ");
                sb.AppendLine(asciiBuilder.ToString());
                asciiBuilder.Length = 0;
            }

            Debug.Write(sb.ToString());
        }
#endif

        public void Dispose()
        {
            _stopCancellationTokenSource.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Core.Packets;
using SS.Utilities;

namespace SS.Core.Modules
{
    public class Network : IModule, IModuleLoaderAware, INetwork, INetworkEncryption, INetworkClient
    {
        private class ListenData
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

        /// <summary>
        /// specialized data buffer which keeps track of what connection it is for and other useful info
        /// </summary>
        private class SubspaceBuffer : DataBuffer
        {
            internal ConnData Conn;

            public byte Tries;
            public NetSendFlags Flags;

            public DateTime LastRetry;

            public ReliableDelegate Callback;
            public object Clos;
            //public IReliableDelegateInvoker CallbackInvoker;  // i was thinking of switching the callback info to use a generic wrapper

            public override void Clear()
            {
                Conn = null;
                Tries = 0;
                Flags = NetSendFlags.None;
                LastRetry = DateTime.MinValue;
                Callback = null;
                Clos = null;

                base.Clear();
            }
        }
        /*
        private interface IReliableDelegateInvoker
        {
            void InvokeCallback(Player p, bool success);
        }

        private class ReliableDelegateWrapper<T> : IReliableDelegateInvoker
        {
            private ReliableDelegate<T> callback;
            private T clos;

            public ReliableDelegateWrapper(ReliableDelegate<T> callback, T clos)
            {
                this.callback = callback;
                this.clos = clos;
            }

            #region IReliableDelegateInvoker Members

            public void InvokeCallback(Player p, bool success)
            {
                callback(p, success, clos);
            }

            #endregion
        }
        */
        private Pool<SubspaceBuffer> _bufferPool = Pool<SubspaceBuffer>.Default;

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
            private GetSizedSendDataDelegate<T> _requestDataCallback;
            private T _clos;
            private int _totalLength;
            private int _offset;

            public SizedSendData(GetSizedSendDataDelegate<T> requestDataCallback, T clos, int totalLength, int offset)
            {
                if (requestDataCallback == null)
                    throw new ArgumentNullException("requestDataCallback");

                _requestDataCallback = requestDataCallback;
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
            public ulong pktSent;

            /// <summary>
            /// # of packets recieved
            /// </summary>
            public ulong pktRecieved;

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
            public ulong relDups;

            /// <summary>
            /// # of reliable retries
            /// </summary>
            public ulong retries;

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

            internal class SizedRecieve
            {
                public int type;
                public int totallen, offset;
            }

            /// <summary>
            /// stuff for recving sized packets, protected by bigmtx
            /// </summary>
            public SizedRecieve sizedrecv = new SizedRecieve();

            internal class BigRecieve
            {
                public int size, room;
                public byte[] buf; //byte *buf; in asss

                internal void free()
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

            public LinkedList<SubspaceBuffer>[] outlist = new LinkedList<SubspaceBuffer>[5];

            /// <summary>
            /// incoming reliable packets
            /// </summary>
            public SubspaceBuffer[] relbuf = new SubspaceBuffer[Constants.CFG_INCOMING_BUFFER];

            // TODO: some other members here that i dont understand yet

            /// <summary>
            /// outlist mutex
            /// </summary>
            public object olmtx = new object();

            /// <summary>
            /// reliable mutex
            /// </summary>
            public object relmtx = new object();

            /// <summary>
            /// big mutex (bigrecv and sizedrecv)
            /// </summary>
            public object bigmtx = new object();

            public void Initalize(IEncrypt enc, IBWLimit bw)
            {
                this.enc = enc;
                this.bw = bw;
                avgrtt = 200; // an initial guess
                rttdev = 100;
                lastPkt = DateTime.Now;
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
                this.i = i;
                this.enc = enc;
            }
        }

        private ModuleManager _mm;
        private IPlayerData _playerData;
        private IConfigManager _configManager;
        private ILogManager _logManager;
        private IServerTimer _serverTimer;
        private IBandwidthLimit _bandwithLimit;

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
            public int pingrefreshtime;
        }

        private Config _config = new Config();

        /// <summary>
        /// per player data key to ConnData
        /// </summary>
        private int _connKey;

        private Dictionary<EndPoint, Player> _clienthash = new Dictionary<EndPoint, Player>();
        private object _hashmtx = new object();
        
        /// <summary>
        /// this is a helper to group up packets to be send out together as 1 combined packet
        /// </summary>
        private struct GroupedPacket
        {
            private Network _network;
            private byte[] _buf;
            private ArraySegment<byte> _remainingSegment;
            private int _count;

            public GroupedPacket(Network network, byte[] buf)
            {
                _network = network;
                _buf = buf;
                _remainingSegment = new ArraySegment<byte>(_buf, 0, _buf.Length);
                _count = 0;
            }

            public void Init()
            {
                _buf[0] = 0x00;
                _buf[1] = 0x0E;
                _remainingSegment = new ArraySegment<byte>(_buf, 2, _buf.Length - 2);
                _count = 0;
            }

            public void Flush(ConnData conn)
            {
                if (_count == 1)
                {
                    // there's only one in the group, so don't send it
                    // in a group. +3 to skip past the 00 0E and size of
                    // first packet
                    _network.sendRaw(conn, new ArraySegment<byte>(_buf, 3, _remainingSegment.Offset - 3));
                }
                else if (_count > 1)
                {
                    _network.sendRaw(conn, _buf, _remainingSegment.Offset);
                }

                // TODO: record stats
                //if(count > 0 && count < _config.n

                Init();
            }

            public void Send(SubspaceBuffer buffer, ConnData conn)
            {
#if DISABLE_GROUPED_SEND
                _network.sendRaw(conn, buffer.Bytes, buffer.NumBytes);
#else
                if (buffer.NumBytes <= 255) // 255 must be the size limit a grouped packet can store (max 1 byte can represent for the length)
                {
                    if(_remainingSegment.Count < (buffer.NumBytes + 1)) // +1 is for the byte that specifies the length
                    //if (_remainingSegment.Count > (Constants.MaxPacket - 10 - buffer.NumBytes)) // i dont know why asss does the -10
                        Flush(conn); // not enough room in the grouped packet, send it out first to start with a fresh grouped packet

                    _remainingSegment.Array[_remainingSegment.Offset] = (byte)buffer.NumBytes;
                    Array.Copy(buffer.Bytes, 0, _remainingSegment.Array, _remainingSegment.Offset + 1, buffer.NumBytes);

                    _remainingSegment = new ArraySegment<byte>(_remainingSegment.Array, _remainingSegment.Offset + (buffer.NumBytes + 1), _remainingSegment.Count - (buffer.NumBytes + 1));
                    _count++;
                }
                else
                {
                    // can't fit into a grouped packet, send immediately
                    _network.sendRaw(conn, buffer.Bytes, buffer.NumBytes);
                }
#endif
            }
        }

        private delegate void oohandler(SubspaceBuffer buffer);

        private oohandler[] _oohandlers;

        private const int MAXTYPES = 64;

        public Dictionary<int, PacketDelegate> _handlers = new Dictionary<int, PacketDelegate>(MAXTYPES);
        public Dictionary<int, PacketDelegate> _nethandlers = new Dictionary<int, PacketDelegate>(20);
        public Dictionary<int, SizedPacketDelegate> _sizedhandlers = new Dictionary<int, SizedPacketDelegate>(MAXTYPES);

        private const int SELECT_TIMEOUT_MS = 1000;

        private MessagePassingQueue<ConnData> _relqueue = new MessagePassingQueue<ConnData>();

        private bool _stopThreads = false;

        private List<Thread> _threadList = new List<Thread>();
        private List<Thread> _reliableThreads = new List<Thread>();

        /// <summary>
        /// info about sockets this object has created, etc...
        /// </summary>
        private List<ListenData> _listenDataList = new List<ListenData>();

        /// <summary>
        /// Key: connectAs
        /// </summary>
        /// <remarks>for now, only 1 listen data allowed for each connectAs.</remarks>
        private Dictionary<string, ListenData> _listenConnectAsLookup = new Dictionary<string, ListenData>();

        private Socket _clientSocket;

        private NetStats _globalStats = new NetStats();

        public Network()
        {
            _oohandlers = new oohandler[20];

            _oohandlers[0] = null; //00 - nothing
            _oohandlers[1] = null; //01 - key initiation
            _oohandlers[2] = processKeyResponse; //02 - key response
            _oohandlers[3] = processReliable; //03 - reliable
            _oohandlers[4] = processAck; //04 - reliable response
            _oohandlers[5] = processSyncRequest; //05 - time sync request
            _oohandlers[6] = null; //06 - time sync response
            _oohandlers[7] = processDrop; //07 - close connection
            _oohandlers[8] = processBigData; //08 - bigpacket
            _oohandlers[9] = processBigData; //09 - bigpacket2
            _oohandlers[10] = processPresize; //0A - presized data (file transfer)
            _oohandlers[11] = processCancelReq;  //0B - cancel presized
            _oohandlers[12] = processCancel; //0C - presized has been cancelled
            _oohandlers[13] = null; //0D - nothing
            _oohandlers[14] = processGrouped; //0E - grouped
            _oohandlers[15] = null; // 0x0F
            _oohandlers[16] = null; // 0x10
            _oohandlers[17] = null; // 0x11
            _oohandlers[18] = null; // 0x12
            _oohandlers[19] = processSpecial; // 0x13 - cont key response
        }

        #region Socket initialization

        private Socket createSocket(int port, IPAddress bindAddress)
        {
            const int SIO_UDP_CONNRESET = -1744830452;  // since IOControl() takes int instead of uint

            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            // to prevent the exception "An existing connection was forcibly closed by the remote host"
            // http://support.microsoft.com/kb/263823
            byte[] optionInValue = new byte[] { 0, 0, 0, 0 };
            socket.IOControl(SIO_UDP_CONNRESET, optionInValue, null);

            try
            {
                socket.Blocking = false;
            }
            catch (Exception ex)
            {
                // not fatal, just warn
                _logManager.Log(LogLevel.Warn, "<net> can't make socket nonblocking: {0}", ex.Message);
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

        private ListenData createListenDataSockets(int configIndex)
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
                gameSocket = createSocket(gamePort, bindAddress);
            }
            catch (Exception ex)
            {
                _logManager.Log(LogLevel.Error, "<net> unable to create game socket: {0}", ex.Message);
                return null;
            }

            try
            {
                pingSocket = createSocket(pingPort, bindAddress);
            }
            catch (Exception ex)
            {
                gameSocket.Close();
                _logManager.Log(LogLevel.Error, "<net> unable to create ping socket: {0}", ex.Message);
                return null;
            }

            ListenData listenData = new ListenData(gameSocket, pingSocket);

            listenData.AllowVIE = _configManager.GetInt(_configManager.Global, configSection, "AllowVIE", 1) > 0;
            listenData.AllowContinuum = _configManager.GetInt(_configManager.Global, configSection, "AllowCont", 1) > 0;
            listenData.ConnectAs = _configManager.GetStr(_configManager.Global, configSection, "ConnectAs");

            return listenData;
        }

        private bool initializeSockets()
        {
            for(int x=0 ; x<10 ; x++)
            {
                ListenData listenData = createListenDataSockets(x);
                if (listenData == null)
                    continue;

                _listenDataList.Add(listenData);

                if (string.IsNullOrEmpty(listenData.ConnectAs) == false)
                {
                    _listenConnectAsLookup.Add(listenData.ConnectAs, listenData);
                }

                _logManager.Log(LogLevel.Drivel, "<net> listening on {0}", listenData.GameSocket.LocalEndPoint);
            }

            try
            {
                _clientSocket = createSocket(0, IPAddress.Any);
            }
            catch (Exception ex)
            {
                _logManager.Log(LogLevel.Error, "<net> unable to create socket for client connections: {0}", ex.Message);
            }

            return true;
        }

        #endregion

        private void recieveThread()
        {
            List<Socket> socketList = new List<Socket>(_listenDataList.Count * 2 + 1);
            List<Socket> checkReadList = new List<Socket>(_listenDataList.Count * 2 + 1);

            Dictionary<EndPoint, ListenData> gameEndpointLookup = new Dictionary<EndPoint, ListenData>(_listenDataList.Count);
            Dictionary<EndPoint, ListenData> pingEndpointLookup = new Dictionary<EndPoint, ListenData>(_listenDataList.Count);

            foreach (ListenData ld in _listenDataList)
            {
                if(ld.GameSocket != null)
                {
                    socketList.Add(ld.GameSocket);
                    gameEndpointLookup.Add(ld.GameSocket.LocalEndPoint, ld);
                }

                if (ld.PingSocket != null)
                {
                    socketList.Add(ld.PingSocket);
                    pingEndpointLookup.Add(ld.PingSocket.LocalEndPoint, ld);
                }
            }

            if (_clientSocket != null)
                socketList.Add(_clientSocket);

            while (true)
            {
                try
                {
                    if (_stopThreads)
                        return;

                    checkReadList.Clear();
                    checkReadList.AddRange(socketList);

                    Socket.Select(checkReadList, null, null, SELECT_TIMEOUT_MS * 1000);

                    if (_stopThreads)
                        return;

                    foreach (Socket socket in checkReadList)
                    {
                        ListenData listenData;
                        if (gameEndpointLookup.TryGetValue(socket.LocalEndPoint, out listenData))
                        {
                            handleGamePacketRecieved(listenData);
                        }
                        else if(pingEndpointLookup.TryGetValue(socket.LocalEndPoint, out listenData))
                        {
                            handlePingPacketRecieved(listenData);
                        }
                        else if (socket == _clientSocket)
                        {
                            handleClientPacketRecieved();
                        }

                        /* i think that the Dictionary hashtable lookup method is more efficient than looping like this...
                        foreach (ListenData ld in _listenDataList)
                        {
                            if (ld.GameSocket == socket)
                                handleGamePacketRecieved(ld);

                            if (ld.PingSocket == socket)
                                handlePingPacketRecieved(ld);
                        }

                        if (socket == _clientSocket)
                            handleClientPacketRecieved();
                        */
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
        }

        private void sendThread()
        {
            while (_stopThreads == false)
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
                            isOurs(p))
                        {
                            ConnData conn = p[_connKey] as ConnData;
                            if (conn == null)
                                continue;

                            if (Monitor.TryEnter(conn.olmtx))
                            {
                                try
                                {
                                    sendOutgoing(conn);
                                    submitRelStats(p);
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
                DateTime now = DateTime.Now;
                try
                {
                    foreach (Player p in _playerData.PlayerList)
                    {
                        if (p.Status >= PlayerState.Connected &&
                            isOurs(p))
                        {
                            processLagouts(p, now, toKill, toFree);
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
                    ConnData conn = p[_connKey] as ConnData;
                    if (conn == null)
                        continue;

                    // one more time, just to be sure
                    clearBuffers(p);

                    //bwlimit->Free(conn->bw);

                    _playerData.FreePlayer(p);
                }
                toFree.Clear();

                // outgoing packets and lagouts for client connections
                // TODO

                if (_stopThreads)
                    return;

                Thread.Sleep(10); // 1/100 second
            }
        }

        private void relThread()
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
                        processBuffer(buf);

                        Monitor.Enter(conn.relmtx);
                    }
                }
            }
        }

        private byte[] _queueDataBuffer = null;
        private byte[] _queueDataHeader = new byte[6] { 0x00, 0x0A, 0, 0, 0, 0 }; // size of a presized data packet header

        // NOTE: this is optimized slightly more than asss in the sense that it doesn't copy the array to another buffer just to send it off to bufferPacket()
        private bool queueMoreData(object dummy)
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
                    if(!isOurs(p) || p.Status >= PlayerState.TimeWait)
                        continue;

                    ConnData conn = p[_connKey] as ConnData;
                    if (conn == null)
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
                            bufferPacket(conn, new ArraySegment<byte>(buffer, bufferIndex, Constants.ChunkSize + 6), NetSendFlags.PriorityN1 | NetSendFlags.Reliable, null, null);
                            bufferIndex += Constants.ChunkSize;
                            needed -= Constants.ChunkSize;
                        }

                        // copy the header
                        Array.Copy(_queueDataHeader, 0, buffer, bufferIndex, 6);
                        bufferPacket(conn, new ArraySegment<byte>(buffer, bufferIndex, needed + 6), NetSendFlags.PriorityN1 | NetSendFlags.Reliable, null, null);

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

        private void clearBuffers(Player p)
        {
            if (p == null)
                return;

            ConnData conn = p[_connKey] as ConnData;
            if (conn == null)
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
                    if (b.Callback != null)
                    {
                        // this is ugly, but we have to release the outlist mutex
                        // during these callbacks, because the callback might need
                        // to acquire some mutexes of its own, and we want to avoid
                        // deadlock.
                        Monitor.Exit(conn.olmtx);
                        b.Callback(p, false, b.Clos);
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
        private void processLagouts(Player p, DateTime now, LinkedList<Player> toKill, LinkedList<Player> toFree)
        {
            ConnData conn = p[_connKey] as ConnData;
            if (conn == null)
                return;

            // this is used for lagouts and also for timewait
            TimeSpan diff = DateTime.Now - conn.lastPkt;

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
                    int len = Encoding.ASCII.GetBytes(message, 0, message.Length, buf.Bytes, 5);

                    lock (conn.olmtx)
                    {
                        sendRaw(conn, buf.Bytes, len + 5);
                    }

                    _logManager.Log(LogLevel.Info, "<net> [{0}] [pid={1}] player kicked for {2}", p.Name, p.Id, reason);

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

                    sendRaw(conn, buf.Bytes, 2);
                }

                // clear all our buffers
                lock (conn.bigmtx)
                {
                    endSized(p, false);
                }

                clearBuffers(p);

                // tell encryption to forget about him
                if (conn.enc != null)
                {
                    conn.enc.Void(p);
                    _mm.ReleaseInterface<IEncrypt>();
                    conn.enc = null;
                }

                // log message
                _logManager.Log(LogLevel.Info, "<net> [{0}] [pid={1}] disconnected", p.Name, p.Id);

                lock (_hashmtx)
                {
                    if (_clienthash.Remove(conn.sin) == false)
                        _logManager.Log(LogLevel.Error, "<net> internal error: established connection not in hash table");
                }

                toFree.AddLast(p);
            }
        }

        private void submitRelStats(Player p)
        {
            ConnData conn = p[_connKey] as ConnData;
            if (conn == null)
                return;

            //if (_lagc)
            //{
            // TODO:
            //}
        }

        private void sendRaw(ConnData conn, ArraySegment<byte> data)
        {
            int len = data.Count;

            Player p = conn.p;

#if CFG_DUMP_RAW_PACKETS
            dumpPk(string.Format("SEND: {0} bytes to pid ", len, p.Id), data);
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

        private void sendRaw(ConnData conn, byte[] data, int len)
        {
            Debug.Assert(len <= Constants.MaxPacket);

            Player p = conn.p;

#if CFG_DUMP_RAW_PACKETS
            dumpPk(string.Format("SEND: {0} bytes to pid ", len, p.Id), new ArraySegment<byte>(data, 0, len));
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

        private void sendOutgoing(ConnData conn)
        {
            DateTime now = DateTime.Now;

            // use an estimate of the average round-trip time to figure out when to resend a packet
            uint timeout = (uint)(conn.avgrtt + (4 * conn.rttdev));

            int canSend = conn.bw.GetCanBufferPackets();

            clip(ref timeout, 250, 2000);

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
                gp.Init();

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

                        buf.LastRetry = DateTime.Now;
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

        private void clip(ref uint timeout, uint low, uint high)
        {
            if(timeout > high)
                timeout = high;
            else if(timeout < low)
                timeout = low;
        }

        private static bool isOurs(Player p)
        {
            return p.Type == ClientType.Continuum || p.Type == ClientType.VIE;
        }

        private void handleGamePacketRecieved(ListenData ld)
        {
            SubspaceBuffer buffer = _bufferPool.Get();
            Socket s = ld.GameSocket;
            EndPoint recievedFrom = new IPEndPoint(IPAddress.Any, 0);
            buffer.NumBytes = s.ReceiveFrom(buffer.Bytes, buffer.Bytes.Length, SocketFlags.None, ref recievedFrom);

            if (buffer.NumBytes <= 0)
            {
                buffer.Dispose();
                return;
            }

#if CFG_DUMP_RAW_PACKETS
            dumpPk(string.Format("RECV: {0} bytes", buffer.NumBytes), new ArraySegment<byte>(buffer.Bytes, 0, buffer.NumBytes));
#endif

            IPEndPoint remoteEndPoint = recievedFrom as IPEndPoint;
            if (remoteEndPoint == null)
                return;

            Player p;
            if (_clienthash.TryGetValue(remoteEndPoint, out p) == false)
            {
                // this might be a new connection. make sure it's really a connection init packet
                if (isConnectionInitPacket(buffer.Bytes))
                {
                    ConnectionInitCallback.Fire(_mm, remoteEndPoint, buffer.Bytes, buffer.NumBytes, ld);
                }
#if CFG_LOG_STUPID_STUFF
                else if (buffer.NumBytes > 1)
                {
                    _logManager.Log(LogLevel.Drivel, "<net> recvd data ({0:X2} {1:X2} ; {2} bytes) before connection established",
                        buffer.Bytes[0],
                        buffer.Bytes[1],
                        buffer.NumBytes);
                }
                else
                {
                    _logManager.Log(LogLevel.Drivel, "<net> recvd data ({0:X2} ; {1} bytes) before connection established",
                        buffer.Bytes[0],
                        buffer.NumBytes);
                }
#endif
                buffer.Dispose();
                return;
            }

            ConnData conn = p[_connKey] as ConnData;

            if (isConnectionInitPacket(buffer.Bytes))
            {
                // here, we have a connection init, but it's from a
		        // player we've seen before. there are a few scenarios:
                if (p.Status == PlayerState.Connected)
                {
                    // if the player is in S_CONNECTED, it means that
                    // the connection init response got dropped on the
                    // way to the client. we have to resend it.
                    ConnectionInitCallback.Fire(_mm, remoteEndPoint, buffer.Bytes, buffer.NumBytes, ld);
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
                _logManager.Log(LogLevel.Warn, "<net> [pid={0}] packet recieved from bad state {1}", p.Id, p.Status);

                // don't set lastpkt time here

                buffer.Dispose();
                return;
            }

            buffer.Conn = conn;
            conn.lastPkt = DateTime.Now;
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
                _logManager.Log(LogLevel.Malicious, "<net> [pid={0}] failure decrypting packet", p.Id);
                buffer.Dispose();
                return;
            }

#if CFG_DUMP_RAW_PACKETS
            dumpPk(string.Format("RECV: about to process {0} bytes", buffer.NumBytes), new ArraySegment<byte>(buffer.Bytes, 0, buffer.NumBytes));
#endif

            processBuffer(buffer);
        }

        /// <summary>
        /// unreliable packets will be processed before the call returns and freed.
        /// network packets will be processed by the appropriate network handler,
        /// which may free it or not.
        /// </summary>
        /// <param name="buffer"></param>
        private void processBuffer(SubspaceBuffer buffer)
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
                        _logManager.Log(LogLevel.Malicious, "<net> [{0}] [pid={1}] unknown network subtype {2}", conn.p.Name, conn.p.Id, rp.T2);
                    }
                    else
                    {
                        _logManager.Log(LogLevel.Malicious, "<net> (client connection) unknown network subtype {0}", rp.T2);
                    }
                    buffer.Dispose();
                }
            }
            else if (rp.T1 < MAXTYPES)
            {
                try
                {
                    if (conn.p != null)
                    {
                        // player connection
                        PacketDelegate packetDelegate;
                        if (_handlers.TryGetValue(rp.T1, out packetDelegate) == true)
                            packetDelegate(conn.p, buffer.Bytes, buffer.NumBytes);
                        else
                            _logManager.Log(LogLevel.Drivel, "<net> no handler for packet T1 [0x{0:X2}]", rp.T1);
                    }
                    else if (conn.cc != null)
                    {
                        // client connection
                        conn.cc.i.HandlePacket(buffer.Bytes, buffer.NumBytes);
                    }
                }
                finally
                {
                    buffer.Dispose();
                }
            }
            else
            {
                try
                {
                    if (conn.p != null)
                        _logManager.Log(LogLevel.Malicious, "<net> [{0}] [pid={1}] unknown packet type {2}", conn.p.Name, conn.p.Id, rp.T1);
                    else
                        _logManager.Log(LogLevel.Malicious, "<net> (client connection) unknown packet type {0}", rp.T1);
                }
                finally
                {
                    buffer.Dispose();
                }
            }
        }

        private static bool isConnectionInitPacket(byte[] data)
        {
            if (data == null)
                return false;

            ReliablePacket rp = new ReliablePacket(data);
            return (rp.T1 == 0x00) && ((rp.T2 == 0x01) || (rp.T2 == 0x11));
        }

        private void handlePingPacketRecieved(ListenData ld)
        {
            if (ld == null)
                return;

            using (SubspaceBuffer buffer = _bufferPool.Get())
            {
                Socket s = ld.PingSocket;
                EndPoint recievedFrom = new IPEndPoint(IPAddress.Any, 0);
                buffer.NumBytes = s.ReceiveFrom(buffer.Bytes, 4, SocketFlags.None, ref recievedFrom);
                IPEndPoint remoteEndPoint = recievedFrom as IPEndPoint;
                if (remoteEndPoint == null)
                    return;

                // TODO: get stats if past threshold

                // HACK: so that we can actually get something other than 0 ms :)
                //Random random = new Random();
                //int randomDelay = random.Next(100, 200);
                //System.Threading.Thread.Sleep(randomDelay);

                if (buffer.NumBytes == 4)
                {
                    // bytes from recieve
                    buffer.Bytes[4] = buffer.Bytes[0];
                    buffer.Bytes[5] = buffer.Bytes[1];
                    buffer.Bytes[6] = buffer.Bytes[2];
                    buffer.Bytes[7] = buffer.Bytes[3];

                    if (string.IsNullOrEmpty(ld.ConnectAs))
                    {
                        // global
                        // TODO: # players
                        buffer.Bytes[0] = 1;
                        buffer.Bytes[1] = 0;
                        buffer.Bytes[2] = 0;
                        buffer.Bytes[3] = 0;
                    }
                    else
                    {
                        // specific arena/zone
                        // TODO: # players
                        buffer.Bytes[0] = 1;
                        buffer.Bytes[1] = 0;
                        buffer.Bytes[2] = 0;
                        buffer.Bytes[3] = 0;
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

        private void handleClientPacketRecieved()
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
            dumpPk(string.Format("RAW CLIENT DATA: {0} bytes", buffer.NumBytes), new ArraySegment<byte>(buffer.Bytes, 0, buffer.NumBytes));
#endif

            // TODO
        }

        private SubspaceBuffer bufferPacket(ConnData conn, byte[] data, int len, NetSendFlags flags, ReliableDelegate callback, object clos)
        {
            return bufferPacket(conn, new ArraySegment<byte>(data, 0, len), flags, callback, clos);
            /*
            // data has to be able to fit into a reliable packet
            Debug.Assert(len <= Constants.MaxPacket - Constants.ReliableHeaderLen);

            // you can't buffer already-reliable packets
            Debug.Assert(!(data[0] == 0x00 && data[1] == 0x03));

            // reliable packets can't be droppable
            Debug.Assert((flags & (NetSendFlags.Reliable | NetSendFlags.Dropabble)) != (NetSendFlags.Reliable | NetSendFlags.Dropabble));

            BandwidthPriorities pri;
            if ((flags & NetSendFlags.PriorityN1) == NetSendFlags.PriorityN1)
            {
                pri = BandwidthPriorities.UnreiableLow;
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
                    sendRaw(conn, data, len);
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
            buf.LastRetry = DateTime.Now.Subtract(new TimeSpan(0, 0, 100));
            buf.Tries = 0;
            buf.Callback = callback;
            buf.Clos = clos;
            buf.Flags = flags;

            // get data into packet
            if ((flags & NetSendFlags.Reliable) == NetSendFlags.Reliable)
            {
                buf.NumBytes = len + Constants.ReliableHeaderLen;
                ReliablePacket rp = new ReliablePacket(buf.Bytes);
                rp.T1 = 0x00;
                rp.T2 = 0x03;
                rp.SeqNum = conn.s2cn++;
                rp.SetData(data, len);
            }
            else
            {
                buf.NumBytes = len;
                Array.Copy(data, 0, buf.Bytes, 0, len);
            }

            conn.outlist[(int)pri].AddLast(buf);

            return buf;
            */
        }

        private SubspaceBuffer bufferPacket(ConnData conn, ArraySegment<byte> data, NetSendFlags flags, ReliableDelegate callback, object clos)
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
                    sendRaw(conn, data);
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
            buf.LastRetry = DateTime.Now.Subtract(new TimeSpan(0, 0, 100));
            buf.Tries = 0;
            buf.Callback = callback;
            buf.Clos = clos;
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

        private void processKeyResponse(SubspaceBuffer buffer)
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
                    _logManager.LogP(LogLevel.Malicious, "net", conn.p, "got key response packet");
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private void processReliable(SubspaceBuffer buffer)
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
                    _logManager.Log(LogLevel.Drivel, "<net> [{0}] [pid={1}] reliable packet with too big delta ({2} - {3})", conn.p.Name, conn.p.Id, sn, conn.c2sn);
                else
                    _logManager.Log(LogLevel.Drivel, "<net> (client connection) reliable packet with too big delta ({0} - {1})", sn, conn.c2sn);

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
                        bufferPacket(conn, ackBuffer.Bytes, AckPacket.Length, NetSendFlags.Ack, null, null);
                    }
                }

                // add to global rel list for processing
                _relqueue.Enqueue(conn);
            }
        }

        private void processAck(SubspaceBuffer buffer)
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

                    if (b.Callback != null)
                        b.Callback(conn.p, true, b.Clos);

                    if (b.Tries == 1)
                    {
                        int rtt = (int)DateTime.Now.Subtract(b.LastRetry).TotalMilliseconds;
                        if (rtt < 0)
                        {
                            _logManager.Log(LogLevel.Error, "<net> negative rtt ({0}); clock going backwards", rtt);
                            rtt = 100;
                        }

                        int dev = conn.avgrtt - rtt;
                        if (dev < 0)
                            dev = -dev;

                        conn.rttdev = (conn.rttdev * 3 + dev) / 4;
                        conn.avgrtt = (conn.avgrtt * 7 + rtt) / 8;

                        //if (_lagc != null && conn.p != null)
                        //_lagc.RelDelay(conn->p, rtt);
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

        private void processSyncRequest(SubspaceBuffer buffer)
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
                    TimeSyncS2CPacket ts = new TimeSyncS2CPacket(b.Bytes);
                    ts.Initialize();
                    //ts.T1 = 0x00;
                    //ts.T2 = 0x06;
                    ts.ClientTime = cts.Time;
                    ts.ServerTime = ServerTick.Now; // asss does this one in the lock, but i rather lock as short as possbile

                    lock (conn.olmtx)
                    {
                        // note: this bypasses bandwidth limits
                        sendRaw(conn, b.Bytes, TimeSyncS2CPacket.Length);

                        // submit data to lagdata
                        /*if (_lagc != null && conn.p != null)
	                    {
		                    struct TimeSyncData data;
		                    data.s_pktrcvd = conn->pktrecvd;
		                    data.s_pktsent = conn->pktsent;
		                    data.c_pktrcvd = cts->pktrecvd;
		                    data.c_pktsent = cts->pktsent;
		                    data.s_time = ts.servertime;
		                    data.c_time = ts.clienttime;
		                    lagc->TimeSync(conn->p, &data);
	                    }*/
                    }
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private void processDrop(SubspaceBuffer buffer)
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

        private void processBigData(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                if (buffer.NumBytes < 3)
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
                            _logManager.LogP(LogLevel.Malicious, "net", conn.p, "refusing to allocate {0} bytes (> {1})", newsize, Constants.MaxBigPacket);
                        else
                            _logManager.Log(LogLevel.Malicious, "<net> (client connection) refusing to allocate {0} bytes (> {1})", newsize, Constants.MaxBigPacket);

                        conn.bigrecv.free();
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
                            _logManager.LogP(LogLevel.Error, "net", conn.p, "cannot allocate {0} bytes for bigpacket", newsize);
                        else
                            _logManager.Log(LogLevel.Error, "<net> (client connection) cannot allocate {0} bytes for bigpacket", newsize);

                        conn.bigrecv.free();
                        return;
                    }

                    Array.Copy(buffer.Bytes, 2, newbuf, conn.bigrecv.size, buffer.NumBytes - 2);
                    conn.bigrecv.buf = newbuf;
                    conn.bigrecv.size = newsize;

                    ReliablePacket rp = new ReliablePacket(buffer.Bytes);
                    if (rp.T2 == 0x08)
                        return;

                    if (newbuf[0] > 0 && newbuf[0] < MAXTYPES)
                    {
                        if (conn.p != null)
                        {
                            PacketDelegate pd;
                            if (_handlers.TryGetValue(newbuf[0], out pd) == true)
                            {
                                pd(conn.p, newbuf, newsize);
                            }
                        }
                        else
                            conn.cc.i.HandlePacket(newbuf, newsize);
                    }
                    else
                    {
                        if (conn.p != null)
                            _logManager.LogP(LogLevel.Warn, "net", conn.p, "bad type for bigpacket: {0}", newbuf[0]);
                        else
                            _logManager.Log(LogLevel.Warn, "<net> (client connection) bad type for bigpacket: {0}", newbuf[0]);
                    }

                    conn.bigrecv.free();
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private void processPresize(SubspaceBuffer buffer)
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
                            endSized(conn.p, false);
                        }
                    }

                    if (conn.sizedrecv.totallen != size)
                    {
                        _logManager.LogP(LogLevel.Malicious, "net", conn.p, "length mismatch in sized packet");
                        endSized(conn.p, false);
                    }
                    else if ((conn.sizedrecv.offset + buffer.NumBytes - 6) > size)
                    {
                        _logManager.LogP(LogLevel.Malicious, "net", conn.p, "sized packet overflow");
                        endSized(conn.p, false);
                    }
                    else
                    {
                        SizedPacketDelegate spd;
                        if (_sizedhandlers.TryGetValue(conn.sizedrecv.type, out spd) == true)
                        {
                            spd(conn.p, rpData, conn.sizedrecv.offset, size);
                        }

                        conn.sizedrecv.offset += rpData.Count;

                        if (conn.sizedrecv.offset >= size)
                            endSized(conn.p, true); // sized recieve is complete
                    }
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private void processCancelReq(SubspaceBuffer buffer)
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
                    bufferPacket(conn, buffer.Bytes, 2, NetSendFlags.Reliable, null, null);
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private void processCancel(SubspaceBuffer buffer)
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
                        endSized(conn.p, false);
                    }
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        // call with bigmtx locked
        private void endSized(Player p, bool success)
        {
            if (p == null)
                return;

            ConnData conn = p[_connKey] as ConnData;
            if (conn == null)
                return;

            if (conn.sizedrecv.offset != 0)
            {
                int type = conn.sizedrecv.type;
                int arg = success ? conn.sizedrecv.totallen : -1;

                // tell listeners that they're cancelled
                if (type < MAXTYPES)
                {
                    SizedPacketDelegate spd;
                    if (_sizedhandlers.TryGetValue(type, out spd) == true)
                    {
                        spd(p, null, arg, arg);
                    }
                }

                conn.sizedrecv.type = 0;
                conn.sizedrecv.totallen = 0;
                conn.sizedrecv.offset = 0;
            }
        }

        private void processGrouped(SubspaceBuffer buffer)
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
                        processBuffer(b);
                    }

                    pos += len;
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private void processSpecial(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                ConnData conn = buffer.Conn;
                if (conn == null)
                    return;

                if (conn.p == null)
                    return;

                ReliablePacket rp = new ReliablePacket(buffer.Bytes);
                PacketDelegate pd;
                if (_nethandlers.TryGetValue(rp.T2, out pd) == true)
                {
                    pd(conn.p, buffer.Bytes, buffer.NumBytes);
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        #endregion

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get
            {
                return new Type[] {
                    typeof(IPlayerData), 
                    typeof(IConfigManager), 
                    typeof(ILogManager), 
                    typeof(IServerTimer), 
                    typeof(IBandwidthLimit), 
                    //typeof(ILagCollect), 
                };
            }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            _mm = mm;
            _playerData = interfaceDependencies[typeof(IPlayerData)] as IPlayerData;
            _configManager = interfaceDependencies[typeof(IConfigManager)] as IConfigManager;
            _logManager = interfaceDependencies[typeof(ILogManager)] as ILogManager;
            _serverTimer = interfaceDependencies[typeof(IServerTimer)] as IServerTimer;
            _bandwithLimit = interfaceDependencies[typeof(IBandwidthLimit)] as IBandwidthLimit;

            if (_mm == null ||
                _playerData == null ||
                _configManager == null ||
                _logManager == null ||
                _serverTimer == null ||
                _bandwithLimit == null)
            {
                return false;
            }

            _connKey = _playerData.AllocatePlayerData<ConnData>();

            _config.droptimeout = _configManager.GetInt(_configManager.Global, "Net", "DropTimeout", 3000); // centiseconds (ticks)
            _config.maxoutlist = _configManager.GetInt(_configManager.Global, "Net", "MaxOutlistSize", 500);

            // (deliberately) undocumented settings
            _config.maxretries = _configManager.GetInt(_configManager.Global, "Net", "MaxRetries", 15);
            _config.queue_threshold = _configManager.GetInt(_configManager.Global, "Net", "PresizedQueueThreshold", 5);
            _config.queue_packets = _configManager.GetInt(_configManager.Global, "Net", "PresizedQueuePackets", 25);
            int reliableThreadCount = _configManager.GetInt(_configManager.Global, "Net", "ReliableThreads", 1);
            _config.overhead = _configManager.GetInt(_configManager.Global, "Net", "PerPacketOverhead", 28);
            _config.pingrefreshtime = _configManager.GetInt(_configManager.Global, "Net", "PingDataRefreshTime", 200);

            if (initializeSockets() == false)
                return false;

            // recieve thread
            Thread thread = new Thread(recieveThread);
            thread.Start();
            _threadList.Add(thread);

            // send thread
            thread = new Thread(sendThread);
            thread.Start();
            _threadList.Add(thread);

            // reliable threads
            thread = new Thread(relThread);
            thread.Start();
            _reliableThreads.Add(thread);
            _threadList.Add(thread);

            // queue more data thread (sends sized data)
            _serverTimer.SetTimer<object>(queueMoreData, 200, 110, null, null); // TODO: maybe change it to be in its own thread?
            
            mm.RegisterInterface<INetwork>(this);
            mm.RegisterInterface<INetworkClient>(this);
            mm.RegisterInterface<INetworkEncryption>(this);
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            mm.UnregisterInterface<INetwork>();
            mm.UnregisterInterface<INetworkClient>();
            mm.UnregisterInterface<INetworkEncryption>();

            _serverTimer.ClearTimer<object>(queueMoreData, null);

            // stop threads
            _stopThreads = true;
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

            _handlers.Clear();
            _sizedhandlers.Clear();
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

        bool IModuleLoaderAware.PostLoad(ModuleManager mm)
        {
            // NOOP
            return true;
        }

        bool IModuleLoaderAware.PreUnload(ModuleManager mm)
        {
            // TODO: 
            return true;
        }

        #endregion

        #region INetworkEncryption Members

        void INetworkEncryption.ReallyRawSend(IPEndPoint remoteEndpoint, byte[] pkt, int len, object v)
        {
            ListenData ld = v as ListenData;
            if (ld == null)
                throw new ArgumentException("is null or not of type ListenData", "v");

#if CFG_DUMP_RAW_PACKETS
            dumpPk(string.Format("SENDRAW: {0} bytes", len), new ArraySegment<byte>(pkt, 0, len));
#endif

            ld.GameSocket.SendTo(pkt, len, SocketFlags.None, remoteEndpoint);
        }

        Player INetworkEncryption.NewConnection(ClientType clientType, IPEndPoint remoteEndpoint, IEncrypt enc, object v)
        {
            ListenData ld = v as ListenData;
            if (ld == null)
                throw new ArgumentException("is null or not of type ListenData", "v");

            // certain ports may have restrictions on client types
            if ((clientType == ClientType.VIE && !ld.AllowVIE) ||
                (clientType == ClientType.Continuum && !ld.AllowContinuum))
                return null;

            Player p;

            // try to find a matching player for the endpoint
            if (remoteEndpoint != null && _clienthash.TryGetValue(remoteEndpoint, out p))
            {
                /* we found it. if its status is S_CONNECTED, just return the
		         * pid. it means we have to redo part of the connection init. */

                /* whether we return p or not, we still have to drop the
                 * reference to enc that we were given. */
                //_mm.ReleaseInterface<IEncrypt>(

                if (p.Status <= PlayerState.Connected)
                {
                    return p;
                }
                else
                {
                    // otherwise, something is horribly wrong. make a note to this effect
                    _logManager.Log(LogLevel.Error, "<net> [pid={0}] NewConnection called for an established address", p.Id);
                    return null;
                }
            }

            p = _playerData.NewPlayer(clientType);
            ConnData conn = p[_connKey] as ConnData;

            conn.Initalize(enc, _bandwithLimit.New());
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
                _logManager.Log(LogLevel.Drivel, "<net> [pid={0}] new connection from {1}", p.Id, remoteEndpoint);
            }
            else
            {
                _logManager.Log(LogLevel.Drivel, "<net> [pid={0}] new internal connection", p.Id);
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

            if (!isOurs(p))
                return;

            ConnData conn = p[_connKey] as ConnData;
            if (conn == null)
                return;

            // see if we can do it the quick way
            if (len <= (Constants.MaxPacket - Constants.ReliableHeaderLen))
            {
                lock (conn.olmtx)
                {
                    bufferPacket(conn, data, len, flags, null, null);
                }
            }
            else
            {
                // not very efficient to allocate a new list and node just to send to one player
                LinkedList<Player> set = new LinkedList<Player>();
                set.AddLast(p);
                sendToSet(set, data, len, flags);
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
                        isOurs(p))
                    {
                        set.AddLast(p);
                    }
                }
            }
            finally
            {
                _playerData.Unlock();
            }

            sendToSet(set, data, len, flags);
        }

        void INetwork.SendToSet(IEnumerable<Player> set, byte[] data, int len, NetSendFlags flags)
        {
            sendToSet(set, data, len, flags);
        }

        private void sendToSet(IEnumerable<Player> set, byte[] data, int len, NetSendFlags flags)
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
                        sendToSet(set, buffer.Bytes, Constants.ChunkSize + 2, flags); // only the 09 is sent reliably for some reason
                        position += Constants.ChunkSize;
                        len -= Constants.ChunkSize;
                    }

                    // final packet is the 09 (signals the end of the big data)
                    buffer.Bytes[1] = 0x09;
                    Array.Copy(data, position, buffer.Bytes, 2, len);
                    sendToSet(set, buffer.Bytes, len + 2, flags | NetSendFlags.Reliable);
                }
            }
            else
            {
                foreach (Player p in set)
                {
                    ConnData conn = p[_connKey] as ConnData;
                    if (conn == null)
                        continue;

                    if (!isOurs(p))
                        continue;

                    lock (conn.olmtx)
                    {
                        bufferPacket(conn, data, len, flags, null, null);
                    }
                }
            }
        }

        void INetwork.SendWithCallback(Player p, byte[] data, int len, ReliableDelegate callback, object clos)
        {
            ConnData conn = p[_connKey] as ConnData;
            if (conn == null)
                return;

            // we can't handle big packets here
            Debug.Assert(len <= (Constants.MaxPacket - Constants.ReliableHeaderLen));

            if (!isOurs(p))
                return;

            lock (conn.olmtx)
            {
                bufferPacket(conn, data, len, NetSendFlags.Reliable, callback, clos);
            }
        }

        bool INetwork.SendSized<T>(Player p, T clos, int len, GetSizedSendDataDelegate<T> requestCallback)
        {
            if (!isOurs(p))
            {
                _logManager.LogP(LogLevel.Drivel, "net", p, "tried to send sized data to non-udp client");
                return false;
            }

            ConnData conn = p[_connKey] as ConnData;
            if (conn == null)
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
                PacketDelegate d;
                if (_handlers.TryGetValue(pktype, out d) == false)
                {
                    _handlers.Add(pktype, func);
                }
                else
                {
                    d += func;
                    _handlers[pktype] = d;
                }
            }
            else if((pktype & 0xFF) == 0)
            {
                int b2 = pktype >> 8;

                if (b2 >= 0 && !_nethandlers.ContainsKey(b2))
                {
                    _nethandlers.Add(b2, func);
                }
            }
        }

        void INetwork.RemovePacket(int pktype, PacketDelegate func)
        {
            if (pktype >= 0 && pktype < MAXTYPES)
            {
                PacketDelegate d;
                if (_handlers.TryGetValue(pktype, out d))
                {
                    d -= func;

                    if (d == null)
                        _handlers.Remove(pktype);
                    else
                        _handlers[pktype] = d;
                }
            }
            else if ((pktype & 0xFF) == 0)
            {
                int b2 = pktype >> 8;

                if (b2 >= 0 && _nethandlers.ContainsKey(b2))
                {
                    _nethandlers.Remove(b2);
                }
            }
        }

        void INetwork.AddSizedPacket(int pktype, SizedPacketDelegate func)
        {
            if (pktype >= 0 && pktype < MAXTYPES)
            {
                SizedPacketDelegate d;
                if (_sizedhandlers.TryGetValue(pktype, out d) == false)
                {
                    _sizedhandlers.Add(pktype, func);
                }
                else
                {
                    d += func;
                    _sizedhandlers[pktype] = d;
                }
            }
        }

        void INetwork.RemoveSizedPacket(int pktype, SizedPacketDelegate func)
        {
            if (pktype >= 0 && pktype < MAXTYPES)
            {
                SizedPacketDelegate d;
                if (_sizedhandlers.TryGetValue(pktype, out d))
                {
                    d -= func;

                    if (d == null)
                        _sizedhandlers.Remove(pktype);
                    else
                        _sizedhandlers[pktype] = d;
                }
            }
        }

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
            ClientConnection clientConnection = cc as ClientConnection;
            if (clientConnection != null)
            {
                dropConnection(clientConnection);
            }
        }

        #endregion

        private void dropConnection(ClientConnection cc)
        {
        }

#if CFG_DUMP_RAW_PACKETS
        private void dumpPk(string description, ArraySegment<byte> d)
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
    }
}

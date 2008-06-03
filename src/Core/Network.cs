using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Net;
using SS.Utilities;
using SS.Core.Packets;
using System.Threading;
using System.Diagnostics;

namespace SS.Core
{
    public delegate void ConnectionInitDelegate(IPEndPoint remoteEndpoint, byte[] buffer, object v);
    public delegate void PacketDelegate(Player p, byte[] data, int length);
    public delegate void SizedPacketDelegate(Player p, byte[] data, int len, int offset, int totallen);

    public interface INetwork : IComponentInterface
    {
        void AddPacket(int pktype, PacketDelegate func);
        void RemovePacket(int pktype, PacketDelegate func);
        void AddSizedPacket(int pktype, SizedPacketDelegate func);
        void RemoveSizedPacket(int pktype, SizedPacketDelegate func);
    }

    public interface IEncrypt
    {
        /// <summary>
        /// data is encrypted in place
        /// </summary>
        /// <param name="p"></param>
        /// <param name="data"></param>
        /// <returns>length of the resulting data</returns>
        int Encrypt(Player p, byte[] data);

        /// <summary>
        /// data is decrypted in place
        /// </summary>
        /// <param name="p"></param>
        /// <param name="data"></param>
        /// <returns>length of the resulting data</returns>
        int Decrypt(Player p, byte[] data, int len);

        /// <summary>
        /// called when the player disconnects
        /// </summary>
        /// <param name="p"></param>
        void Void(Player p);
    }

    public interface IClientConn
    {
        void Connected();
        void HandlePacket(byte[] pkt, int len);
        void Disconnected();
    }

    // looks like asss uses this one like a void* type
    public interface IClientEncrypt
    {
    }

    public interface INetworkClient : IComponentInterface
    {
        BaseClientConnection MakeClientConnection(string address, int port, IClientConn icc, IClientEncrypt ice);
        void SendPacket(BaseClientConnection cc, byte[] pkt, int len, int flags);
        void DropConnection(BaseClientConnection cc);
    }

    public interface INetworkEncryption : IComponentInterface
    {
        void ReallyRawSend(IPEndPoint remoteEndpoint, byte[] pkt, int len, object v);
        Player NewConnection(ClientType clientType, IPEndPoint remoteEndpoint, IEncrypt enc, object v);
    }

    [Flags]
    public enum NetSendFlags : byte
    {
        Unreliable = 0x00, 
        Reliable = 0x01, 
        Dropabble = 0x02, 
        Urgent = 0x04, 

        PriorityN1 = 0x10, 
        PriorityDefault = 0x20, 
        PriorityP1 = 0x30, 
        PriorityP2 = 0x40, 
        PriorityP3 = 0x50, 
        PriorityP4 = 0x64, // includes urgent flag
        PriorityP5 = 0x74, // includes urgent flag
    }

    public class Network : IModule, IModuleLoaderAware, INetwork, INetworkEncryption, INetworkClient
    {
        private class SubspaceBuffer : PooledObject
        {
            internal ConnData conn;
            short len;

            public byte tries;
            public byte flags;

            public DateTime lastretry;

            public readonly byte[] Bytes = new byte[Constants.MaxPacket+4]; // asss does MAXPACKET+4 and i'm not sure why
            public int NumBytes;

            protected override void Dispose(bool isDisposing)
            {
                // clear

                // return object to its pool
                base.Dispose(isDisposing);
            }
        }

        private Pool<SubspaceBuffer> _bufferPool = new Pool<SubspaceBuffer>();

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
            IPEndPoint sin;

            /// <summary>
            /// which of our sockets to use when sending
            /// </summary>
            public Socket whichSock;

            /// <summary>
            /// sequence number for reliable packets
            /// </summary>
            int s2cn;

            /// <summary>
            /// sequence number for reliable packets
            /// </summary>
            int c2sn;

            /// <summary>
            /// time of last packet recvd and of initial connection
            /// </summary>
            public DateTime lastPkt;

            /// <summary>
            /// # of packets sent
            /// </summary>
            ulong pktSent;

            /// <summary>
            /// # of packets recieved
            /// </summary>
            public ulong pktRecieved;

            /// <summary>
            /// # of bytes sent
            /// </summary>
            ulong bytesSent;

            /// <summary>
            /// # of bytes recieved
            /// </summary>
            public ulong bytesRecieved;

            /// <summary>
            /// # of duplicate reliable packets
            /// </summary>
            ulong relDups;

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
            byte unused1;
            byte unused2;

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

            internal struct SizedRecieve
            {
                int type;
                int totallen, offset;
            }

            /// <summary>
            /// stuff for recving sized packets, protected by bigmtx
            /// </summary>
            SizedRecieve sizedrecv;

            internal struct BigRecieve
            {
                int size, room;
                byte buff; //byte *buf; in asss
            }

            /// <summary>
            /// stuff for recving big packets, protected by bigmtx
            /// </summary>
            BigRecieve bigrecv;

            /// <summary>
            /// stuff for sending sized packets, protected by olmtx
            /// </summary>
            LinkedList<object> sizedsends = new LinkedList<object>();

            /// <summary>
            /// bandwidth limiting
            /// </summary>
            public IBWLimit bw;

            public LinkedList<SubspaceBuffer>[] outlist = new LinkedList<SubspaceBuffer>[5];

            SubspaceBuffer[] relbuf = new SubspaceBuffer[Constants.CFG_INCOMING_BUFFER];

            // TODO: some other members here that i dont understand yet

            /// <summary>
            /// outlist mutex
            /// </summary>
            public object olmtx = new object();

            object relmtx = new object();
            object bigmtx = new object();

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

        private int _connKey;

        private Dictionary<EndPoint, Player> _clienthash = new Dictionary<EndPoint, Player>();
        private object _hashmtx = new object();

        private Config _config = new Config();

        // TODO: think about how to send a reference to this class as well (in listenData?)
        //public delegate void DataRecievedHandler(ListenData listenData, SubspaceBuffer buffer);
        //public event DataRecievedHandler GameDataRecieved;
        //public event DataRecievedHandler PingDataRecieved;

        private delegate void oohandler(SubspaceBuffer buffer);

        private oohandler[] _oohandlers;
        /*
        {
            null, //00 - nothing
            null, //01 - key initiation
            processKeyResponse, //02 - key response
            processReliable, //03 - reliable
            processAck, //04 - reliable response
            processSyncRequest, //05 - time sync request
            null, //06 - time sync response
            processDrop, //07 - close connection
            processBigData, //08 - bigpacket
            processBigData, //09 - bigpacket2
            processPresize, //0A - presized data (file transfer)
            processCancelReq, //0B - cancel presized
            processCancel, //0C - presized has been cancelled
            null, //0D - nothing
            processGrouped, //0E - grouped
            null, // 0x0F
            null, // 0x10
            null, // 0x11
            null, // 0x12
            processSpecial, // 0x13 - cont key response
            null
        };*/

        #region oohandlers (network layer header handling)

        private void processKeyResponse(SubspaceBuffer buffer)
        {
        }

        private void processReliable(SubspaceBuffer buffer)
        {
        }

        private void processAck(SubspaceBuffer buffer)
        {
        }

        private void processSyncRequest(SubspaceBuffer buffer)
        {
        }

        private  void processDrop(SubspaceBuffer buffer)
        {
        }

        private void processBigData(SubspaceBuffer buffer)
        {
        }

        private void processPresize(SubspaceBuffer buffer)
        {
        }

        private void processCancelReq(SubspaceBuffer buffer)
        {
        }

        private  void processCancel(SubspaceBuffer buffer)
        {
        }

        private void processGrouped(SubspaceBuffer buffer)
        {
        }

        private void processSpecial(SubspaceBuffer buffer)
        {
        }

        #endregion

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

        private const int MAXTYPES = 64;

        public Dictionary<int, PacketDelegate> _handlers = new Dictionary<int, PacketDelegate>(MAXTYPES);
        public Dictionary<int, PacketDelegate> _nethandlers = new Dictionary<int, PacketDelegate>(20);
        public Dictionary<int, SizedPacketDelegate> _sizedhandlers = new Dictionary<int, SizedPacketDelegate>(MAXTYPES);

        private const int SELECT_TIMEOUT_MS = 1000;

        private bool _stopThreads = false;

        private List<Thread> _threadList = new List<Thread>();

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

        private NetStats _globalStats;

        //private BufferPool<SubspaceBuffer> _bufferPool = new BufferPool<SubspaceBuffer>();
        /*
        public Network(BufferPool<SubspaceBuffer> bufferPool)
        {
            _bufferPool = bufferPool;
        }
        */
        private Socket createSocket(int port, IPAddress bindAddress)
        {
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

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
                    gameEndpointLookup.Add(ld.PingSocket.LocalEndPoint, ld);
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
                            handleGamePacketRecieved(listenData);
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
            // TODO
        }

        private void clearBuffers(Player p)
        {
            
        }

        private void processLagouts(Player p, DateTime now, LinkedList<Player> toKill, LinkedList<Player> toFree)
        {
            
        }

        private void submitRelStats(Player p)
        {
            
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
            ArraySegment<int> i = new ArraySegment<int>(null, 0, 0);

            GroupedPacket gp = new GroupedPacket();
            gp.Init();

            int outlistlen = 0;
            int retries = 0;

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
                    if ((buf.tries != 0) && ((now - buf.lastretry).TotalMilliseconds <= (timeout * buf.tries)))
                        continue;

                    // only buffer fixed number of rel packets to client
                    int seqNum = rp.SeqNum;
                    if ((pri == (int)BandwidthPriorities.Reliable) && ((seqNum - minseqnum) > canSend))
                        continue;

                    // if we've retried too many times, kick the player
                    if (buf.tries > _config.maxretries)
                    {
                        conn.hitmaxretries = true;
                        return;
                    }

                    // at this point, there's only one more check to determine if we're sending this packet now: bandwidth limiting.
                    if (conn.bw.Check(
                        buf.NumBytes + ((buf.NumBytes <= 255) ? 1 : _config.overhead),
                        pri))
                    {
                        // try dropping it, if we can
                        if ((buf.flags & (byte)NetSendFlags.Dropabble) != 0)
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

                    if (buf.tries > 0)
                    {
                        // this is a retry, not an initial send. record it for
                        // lag stats and also reduce bw limit (with clipping)
                        retries++;
                        conn.bw.AdjustForRetry();
                    }

                    buf.lastretry = DateTime.Now;
                    buf.tries++;

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

            conn.retries += (uint)retries;

            if (outlistlen > _config.maxoutlist)
                conn.hitmaxoutlist = true;
        }

        /// <summary>
        /// this is a helper to group up packets to be send out together as 1 combined packet
        /// </summary>
        private struct GroupedPacket
        {
            private Network _network;
            //private SubspaceBuffer _sb;
            private byte[] _buf;
            private ArraySegment<byte> _remainingSegment;
            private int _count;
            
            public GroupedPacket(Network network, byte[] buf)
            {
                _network = network;
                //_sb = _network._bufferPool.Get();
                _buf = buf;
                _remainingSegment = new ArraySegment<byte>(_buf, 0, _buf.Length);
                _count = 0;
            }
            
            public void Init()
            {
                _buf[0] = 0x00;
                _buf[1] = 0x0E;
                _remainingSegment = new ArraySegment<byte>(_buf, _remainingSegment.Offset + 2, _remainingSegment.Count - 2);
                _count = 0;
            }

            public void Flush(ConnData conn)
            {
                if (_count == 1)
                {
                    // there's only one in the group, so don't send it
                    // in a group. +3 to skip past the 00 0E and size of
                    // first packet
                    //SendRaw(
                }
                else if (_count > 1)
                {
                    //SendRaw(
                }

                // TODO: record stats
                //if(count > 0 && count < _config.n

                Init();
            }

            public void Send(SubspaceBuffer buffer, ConnData conn)
            {
                if (buffer.NumBytes <= 255) // 255 must be the size limit a grouped packet can store (max 1 byte can represent for the length)
                {
                    if(_remainingSegment.Count > (Constants.MaxPacket - 10 - buffer.NumBytes))
                        Flush(conn);

                    _remainingSegment.Array[_remainingSegment.Offset] = (byte)buffer.NumBytes;
                    Array.Copy(buffer.Bytes, 0, _remainingSegment.Array, _remainingSegment.Offset + 1, buffer.NumBytes);

                    _remainingSegment = new ArraySegment<byte>(_remainingSegment.Array, _remainingSegment.Offset + (buffer.NumBytes + 1), _remainingSegment.Count - (buffer.NumBytes + 1));
                    _count++;
                }
                else
                {
                    // can't fit in group, send immediately
                    //SendRaw(
                }
            }
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
            return p.Type == ClientType.Continuum || p.Type == ClientType.Vie;
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

            IPEndPoint remoteEndPoint = recievedFrom as IPEndPoint;
            if (remoteEndPoint == null)
                return;

            Player p;
            if (_clienthash.TryGetValue(remoteEndPoint, out p) == false)
            {
                // this might be a new connection. make sure it's really a connection init packet
                if (isConnectionInitPacket(buffer.Bytes))
                {
                    _mm.DoCallbacks(Constants.Events.ConnectionInit, remoteEndPoint, buffer.Bytes, ld);
                }
#if TRACE
                else if (buffer.NumBytes > 1)
                {
                    _logManager.Log(LogLevel.Drivel, "<net> recvd data ({0:X} {1:X} ; {2} bytes) before connection established",
                        buffer.Bytes[0],
                        buffer.Bytes[1],
                        buffer.NumBytes);
                }
                else
                {
                    _logManager.Log(LogLevel.Drivel, "<net> recvd data ({0:X} ; {1} bytes) before connection established",
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
                    _mm.DoCallbacks(Constants.Events.ConnectionInit, remoteEndPoint, buffer.Bytes, ld);
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

            buffer.conn = conn;
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
            ConnData conn = buffer.conn;
            ReliablePacket rp = new ReliablePacket(buffer.Bytes);
            if (rp.T1 == 0x00)
            {
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
                if (conn.p != null)
                {
                    // player connection
                    PacketDelegate packetDelegate;
                    if (_handlers.TryGetValue(rp.T1, out packetDelegate) == true)
                        packetDelegate(conn.p, buffer.Bytes, buffer.NumBytes);
                }
                else if (conn.cc != null)
                {
                    // client connection
                    conn.cc.i.HandlePacket(buffer.Bytes, buffer.NumBytes);

                    buffer.Dispose();
                }
            }
            else
            {
                if (conn.p != null)
                    _logManager.Log(LogLevel.Malicious, "<net> [{0}] [pid={1}] unknown packet type {2}", conn.p.Name, conn.p.Id, rp.T1);
                else
                    _logManager.Log(LogLevel.Malicious, "<net> (client connection) unknown packet type {0}", rp.T1);

                buffer.Dispose();
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
            using (SubspaceBuffer buffer = _bufferPool.Get())
            {
                Socket s = ld.PingSocket;
                EndPoint recievedFrom = new IPEndPoint(IPAddress.Any, 0);
                buffer.NumBytes = s.ReceiveFrom(buffer.Bytes, 4, SocketFlags.None, ref recievedFrom);
                IPEndPoint remoteEndPoint = recievedFrom as IPEndPoint;
                if (remoteEndPoint == null)
                    return;
                
                // TODO: add ability handle ASSS' extended ping packets
                if (buffer.NumBytes != 4)
                {
                    return;
                }

                // HACK: so that we can actually get something other than 0 ms :)
                Random random = new Random();
                int randomDelay = random.Next(100, 200);
                System.Threading.Thread.Sleep(randomDelay);

                // bytes from recieve
                buffer.Bytes[4] = buffer.Bytes[0];
                buffer.Bytes[5] = buffer.Bytes[1];
                buffer.Bytes[6] = buffer.Bytes[2];
                buffer.Bytes[7] = buffer.Bytes[3];

                // # players
                buffer.Bytes[0] = 1;
                buffer.Bytes[1] = 0;
                buffer.Bytes[2] = 0;
                buffer.Bytes[3] = 0;

                int bytesSent = s.SendTo(buffer.Bytes, 8, SocketFlags.None, remoteEndPoint);

                //if (PingDataRecieved != null)
                    //PingDataRecieved(ld, buffer);
            }
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

            // TODO
        }

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

            _config.droptimeout = _configManager.GetInt(_configManager.Global, "Net", "DropTimeout", 3000);
            _config.maxoutlist = _configManager.GetInt(_configManager.Global, "Net", "MaxOutlistSize", 200);

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

            // stop threads
            _stopThreads = true;
            foreach (Thread thread in _threadList)
            {
                thread.Join();
            }
            _threadList.Clear();

            _handlers.Clear();
            _sizedhandlers.Clear();
            //_relqueue.Clear();

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

            ld.GameSocket.SendTo(pkt, len, SocketFlags.None, remoteEndpoint);
        }

        Player INetworkEncryption.NewConnection(ClientType clientType, IPEndPoint remoteEndpoint, IEncrypt enc, object v)
        {
            ListenData ld = v as ListenData;
            if (ld == null)
                throw new ArgumentException("is null or not of type ListenData", "v");

            // certain ports may have restrictions on client types
            if ((clientType == ClientType.Vie && !ld.AllowVIE) ||
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
                case ClientType.Vie:
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
    }
}

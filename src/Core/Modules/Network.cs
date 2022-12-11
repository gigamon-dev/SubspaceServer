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
    /// Module that provides functionality to communicate using UDP and Subspace 'core' procotol.
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
        /// <summary>
        /// specialized data buffer which keeps track of what connection it is for and other useful info
        /// </summary>
        private class SubspaceBuffer : DataBuffer
        {
            public ConnData Conn;

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

                if (CallbackInvoker != null)
                {
                    CallbackInvoker.Dispose();
                    CallbackInvoker = null;
                }

                base.Clear();
            }
        }

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

        private Pool<SubspaceBuffer> _bufferPool;
        private Pool<BigReceive> _bigReceivePool;
        private Pool<ReliableCallbackInvoker> _reliableCallbackInvokerPool;
        private NonTransientObjectPool<LinkedListNode<SubspaceBuffer>> _bufferNodePool;

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

        [Flags]
        private enum PingPopulationMode
        {
            Total = 1,
            Playing = 2,
        }

        private class Config
        {
            /// <summary>
            /// How long to get no data from a client before disconnecting him.
            /// </summary>
            [ConfigHelp("Net", "DropTimeout", ConfigScope.Global, typeof(int), DefaultValue = "3000",
                Description = "How long to get no data from a client before disconnecting him (in ticks).")]
            public TimeSpan DropTimeout;

            /// <summary>
            /// How many S2C packets the server will buffer for a client before dropping him.
            /// </summary>
            [ConfigHelp("Net", "MaxOutlistSize", ConfigScope.Global, typeof(int), DefaultValue = "500",
                Description = "How many S2C packets the server will buffer for a client before dropping him.")]
            public int MaxOutlistSize;

            /// <summary>
            /// if we haven't sent a reliable packet after this many tries, drop the connection
            /// </summary>
            public int MaxRetries;

            /// <summary>
            /// Whether to limit the size of grouped reliable packets to be able to fit into another grouped packet.
            /// </summary>
            public bool LimitReliableGroupingSize;

            /// <summary>
            /// For sized sends (sending files), the threshold to start queuing up more packets.
            /// </summary>
            public int PresizedQueueThreshold;

            /// <summary>
            /// # of additional packets that can be queued up when below the <see cref="PresizedQueueThreshold"/>.
            /// Note: This means, the maximum # of packets that will be queued up at a given time is <see cref="PresizedQueueThreshold"/> - 1 + <see cref="PresizedQueuePackets"/>.
            /// </summary>
            public int PresizedQueuePackets;

            /// <summary>
            /// ip/udp overhead, in bytes per physical packet
            /// </summary>
            public int PerPacketOverhead;

            /// <summary>
            /// How often to refresh the ping packet data.
            /// </summary>
            public TimeSpan PingRefreshThreshold;

            /// <summary>
            /// display total or playing in simple ping responses
            /// </summary>
            [ConfigHelp("Net", "SimplePingPopulationMode", ConfigScope.Global, typeof(int), DefaultValue = "1",
                Description =
                "Display what value in the simple ping reponse (used by continuum)?" +
                "1 = display total player count(default);" +
                "2 = display playing count(in ships);" +
                "3 = alternate between 1 and 2")]
            public PingPopulationMode SimplePingPopulationMode;
        }

        private readonly Config _config = new();

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

        private readonly PingData _pingData = new();

        /// <summary>
        /// per player data key to ConnData
        /// </summary>
        private PlayerDataKey<ConnData> _connKey;

        private readonly ConcurrentDictionary<EndPoint, Player> _clienthash = new();

        private readonly Dictionary<EndPoint, NetClientConnection> _clientConnections = new();
        private readonly ReaderWriterLockSlim _clientConnectionsLock = new(LockRecursionPolicy.NoRecursion);


        /// <summary>
        /// A helper to group up packets to be send out together as 1 combined packet.
        /// </summary>
        private ref struct PacketGrouper
        {
            private readonly Network network;
            private readonly Span<byte> bufferSpan;
            private Span<byte> remainingSpan;
            private int count;
            public int Count => count;
            private int numBytes;
            public int NumBytes => numBytes;

            public PacketGrouper(Network network, Span<byte> bufferSpan)
            {
                if (bufferSpan.Length < 4)
                    throw new ArgumentException("Needs a minimum length of 4 bytes.", nameof(bufferSpan));

                this.network = network ?? throw new ArgumentNullException(nameof(network));
                this.bufferSpan = bufferSpan;

                this.bufferSpan[0] = 0x00;
                this.bufferSpan[1] = 0x0E;
                remainingSpan = this.bufferSpan.Slice(2, Math.Min(bufferSpan.Length, Constants.MaxGroupedPacketLength) - 2);
                count = 0;
                numBytes = 2;
            }

            public void Initialize()
            {
                remainingSpan = this.bufferSpan.Slice(2, Math.Min(bufferSpan.Length, Constants.MaxGroupedPacketLength) - 2);
                count = 0;
                numBytes = 2;
            }

            public void Flush(ConnData conn)
            {
                if (conn == null)
                    throw new ArgumentNullException(nameof(conn));

                if (count == 1)
                {
                    // there's only one in the group, so don't send it in a group. 
                    // +3 to skip past the 00 0E and size of first packet
                    network.SendRaw(conn, bufferSpan.Slice(3, numBytes - 3));
                }
                else if (count > 1)
                {
                    network.SendRaw(conn, bufferSpan.Slice(0, numBytes));
                }

                // record stats about grouped packets
                if (count > 0)
                {
                    Interlocked.Increment(ref network._globalStats.GroupedStats[Math.Min((count - 1), network._globalStats.GroupedStats.Length - 1)]);
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

                return remainingSpan.Length >= length + 1; // +1 is for the byte that specifies the length
            }

            public bool TryAppend(ReadOnlySpan<byte> data)
            {
                if (data.Length == 0)
                    throw new ArgumentOutOfRangeException(nameof(data), "Length must be > 0.");

                if (data.Length > Constants.MaxGroupedPacketItemLength)
                    return false;

                int lengthWithHeader = data.Length + 1; // +1 is for the byte that specifies the length
                if (remainingSpan.Length < lengthWithHeader)
                    return false;

                remainingSpan[0] = (byte)data.Length;
                data.CopyTo(remainingSpan[1..]);

                remainingSpan = remainingSpan[lengthWithHeader..];
                numBytes += lengthWithHeader;
                count++;

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
                    if (remainingSpan.Length < lengthWithHeader)
                        Flush(conn); // not enough room in the grouped packet, send it out first, to start with a fresh grouped packet

                    remainingSpan[0] = (byte)data.Length;
                    data.CopyTo(remainingSpan.Slice(1));

                    remainingSpan = remainingSpan.Slice(lengthWithHeader);
                    numBytes += lengthWithHeader;
                    count++;
                    return;
                }
#endif                
                // can't fit into a grouped packet, send immediately
                network.SendRaw(conn, data);
            }
        }

        /// <summary>
        /// Handlers for 'core' packets (ss protocol's network/transport layer).
        /// </summary>
        /// <remarks>
        /// The first byte of these packets is 0x00.
        /// The second byte identifies the type and is the index into this array.
        /// </remarks>
        private readonly Action<SubspaceBuffer>[] _oohandlers;

        private const int MAXTYPES = 64;

        /// <summary>
        /// Handlers for 'game' packets.
        /// </summary>
        private readonly PacketDelegate[] _handlers = new PacketDelegate[MAXTYPES];
        private readonly PacketDelegate[] _nethandlers = new PacketDelegate[0x14];
        private readonly SizedPacketDelegate[] _sizedhandlers = new SizedPacketDelegate[MAXTYPES];

        private const int MICROSECONDS_PER_MILLISECOND = 1000;

        private readonly MessagePassingQueue<ConnData> _relqueue = new();

        private readonly CancellationTokenSource _stopCancellationTokenSource = new();
        private CancellationToken _stopToken;

        private readonly List<Thread> _threadList = new();
        private readonly List<Thread> _reliableThreads = new();

        /// <summary>
        /// info about sockets this object has created, etc...
        /// </summary>
        private readonly List<ListenData> _listenDataList;
        private readonly ReadOnlyCollection<ListenData> _readOnlyListenData;

        private Socket _clientSocket;

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

        private readonly NetStats _globalStats = new();

        public Network()
        {
            _oohandlers = new Action<SubspaceBuffer>[20];

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

            if (bindAddress == null)
                bindAddress = IPAddress.Any;

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

        [ConfigHelp("Listen", "Port", ConfigScope.Global, typeof(int),
            "The port that the game protocol listens on. Sections named " +
            "Listen1, Listen2, ... are also supported. All Listen " +
            "sections must contain a port setting.")]
        [ConfigHelp("Listen", "BindAddress", ConfigScope.Global, typeof(string),
            "The interface address to bind to. This is optional, and if " +
            "omitted, the server will listen on all available interfaces.")]
        private ListenData CreateListenDataSockets(int configIndex)
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

        [ConfigHelp("Net", "InternalClientPort", ConfigScope.Global, typeof(int),
            Description = "The bind port for the internal client socket (used to communicate with biller and dirserver).")]
        private bool InitializeSockets()
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
        }

        #endregion

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
                        conn.c2sn++;
                        conn.relbuf[spot] = null;
                        processedCount++;

                        // don't need the mutex while doing the actual processing
                        Monitor.Exit(conn.relmtx);

                        // process it
                        buf.NumBytes -= ReliableHeader.Length;
                        Array.Copy(buf.Bytes, ReliableHeader.Length, buf.Bytes, 0, buf.NumBytes);
                        ProcessBuffer(buf);

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

        private byte[] _queueDataBuffer = null;
        private readonly byte[] _queueDataHeader = new byte[6] { 0x00, 0x0A, 0, 0, 0, 0 }; // size of a presized data packet header

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
                            BufferPacket(conn, new ReadOnlySpan<byte>(buffer, bufferIndex, Constants.ChunkSize + 6), NetSendFlags.PriorityN1 | NetSendFlags.Reliable);
                            bufferIndex += Constants.ChunkSize;
                            needed -= Constants.ChunkSize;
                        }

                        Array.Copy(_queueDataHeader, 0, buffer, bufferIndex, 6); // write the header in front of the data
                        BufferPacket(conn, new ReadOnlySpan<byte>(buffer, bufferIndex, needed + 6), NetSendFlags.PriorityN1 | NetSendFlags.Reliable);

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

        private void ClearBuffers(Player player)
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
                        QueueMainloopWorkItem(b.CallbackInvoker, player, false);
                        b.CallbackInvoker = null; // the workitem is now responsible for disposing the callback invoker
                    }

                    outlist.Remove(node);
                    _bufferNodePool.Return(node);
                    b.Dispose();
                }
            }
        }

        // call with player status locked
        private void ProcessLagouts(Player player, DateTime now, List<Player> toKick, List<Player> toFree)
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
                buf.NumBytes = 5 + Encoding.ASCII.GetBytes(message, 0, message.Length, buf.Bytes, 5);

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

                if (_clienthash.TryRemove(conn.RemoteEndpoint, out _) == false)
                    _logManager.LogM(LogLevel.Error, nameof(Network), "Established connection not in hash table.");

                toFree.Add(player);
            }
        }

        private void SubmitRelStats(Player player)
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

            encryptedBuffer = encryptedBuffer.Slice(0, len);

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

        private void SendOutgoing(ConnData conn, ref PacketGrouper packetGrouper)
        {
            DateTime now = DateTime.UtcNow;

            // use an estimate of the average round-trip time to figure out when to resend a packet
            uint timeout = (uint)(conn.avgrtt + (4 * conn.rttdev));
            Clip(ref timeout, 250, 2000);

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

        private static void Clip(ref uint timeout, uint low, uint high)
        {
            if (timeout > high)
                timeout = high;
            else if (timeout < low)
                timeout = low;
        }

        private static bool IsOurs(Player player)
        {
            return player.Type == ClientType.Continuum || player.Type == ClientType.VIE;
        }

        private void HandleGamePacketReceived(ListenData ld)
        {
            SubspaceBuffer buffer = _bufferPool.Get();
            IPEndPoint remoteIPEP = _objectPoolManager.IPEndPointPool.Get();
            Player p;
            ConnData conn;

            try
            {
                EndPoint remoteEP = remoteIPEP;

                try
                {
                    buffer.NumBytes = ld.GameSocket.ReceiveFrom(buffer.Bytes, buffer.Bytes.Length, SocketFlags.None, ref remoteEP);
                }
                catch (SocketException ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"SocketException with error code {ex.ErrorCode} when receiving from game socket {ld.GameSocket.LocalEndPoint}. {ex}");
                    buffer.Dispose();
                    return;
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"Exception when receiving from game socket {ld.GameSocket.LocalEndPoint}. {ex}");
                    buffer.Dispose();
                    return;
                }

                if (buffer.NumBytes <= 0)
                {
                    buffer.Dispose();
                    return;
                }

#if CFG_DUMP_RAW_PACKETS
                DumpPk($"RECV: {buffer.NumBytes} bytes", buffer.Bytes.AsSpan(0, buffer.NumBytes));
#endif

                if (!ReferenceEquals(remoteIPEP, remoteEP))
                {
                    // Note: This seems to be the normal behavior.
                    // No idea why it makes you pass in a reference to an EndPoint if it's just going to replace it, but perhaps it isn't always the case.
                    _objectPoolManager.IPEndPointPool.Return(remoteIPEP);
                    remoteIPEP = null;
                }

                if (remoteEP is not IPEndPoint remoteEndPoint)
                {
                    buffer.Dispose();
                    return;
                }

                if (_clienthash.TryGetValue(remoteEndPoint, out p) == false)
                {
                    // this might be a new connection. make sure it's really a connection init packet
                    if (IsConnectionInitPacket(buffer.Bytes))
                    {
                        ProcessConnectionInit(remoteEndPoint, buffer.Bytes, buffer.NumBytes, ld);
                    }
#if CFG_LOG_STUPID_STUFF
                    else if (buffer.NumBytes > 1)
                    {
                        _logManager.LogM(LogLevel.Drivel, nameof(Network), $"Received data ({buffer.Bytes[0]:X2} {buffer.Bytes[1]:X2} ; {buffer.NumBytes} bytes) before connection established.");
                    }
                    else
                    {
                        _logManager.LogM(LogLevel.Drivel, nameof(Network), $"Received data ({buffer.Bytes[0]:X2} ; {buffer.NumBytes} bytes) before connection established.");
                    }
#endif
                    buffer.Dispose();
                    return;
                }

                if (!p.TryGetExtraData(_connKey, out conn))
                {
                    buffer.Dispose();
                    return;
                }

                if (IsConnectionInitPacket(buffer.Bytes))
                {
                    // here, we have a connection init, but it's from a
                    // player we've seen before. there are a few scenarios:
                    if (p.Status == PlayerState.Connected)
                    {
                        // if the player is in PlayerState.Connected, it means that
                        // the connection init response got dropped on the
                        // way to the client. we have to resend it.
                        ProcessConnectionInit(remoteEndPoint, buffer.Bytes, buffer.NumBytes, ld);
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
            }
            finally
            {
                if (remoteIPEP != null)
                    _objectPoolManager.IPEndPointPool.Return(remoteIPEP);
            }

            if (buffer.NumBytes > Constants.MaxPacket)
            {
                buffer.NumBytes = Constants.MaxPacket;
            }

            // we shouldn't get packets in this state, but it's harmless if we do
            if (p.Status >= PlayerState.LeavingZone || p.WhenLoggedIn >= PlayerState.LeavingZone)
            {
                buffer.Dispose();
                return;
            }

            if (p.Status > PlayerState.TimeWait)
            {
                _logManager.LogM(LogLevel.Warn, nameof(Network), $"[pid={p.Id}] Packet received from bad state {p.Status}.");

                // don't set lastpkt time here

                buffer.Dispose();
                return;
            }

            buffer.Conn = conn;
            conn.lastPkt = DateTime.UtcNow;
            conn.bytesReceived += (ulong)buffer.NumBytes;
            conn.pktReceived++;
            Interlocked.Add(ref _globalStats.byterecvd, (ulong)buffer.NumBytes);
            Interlocked.Increment(ref _globalStats.pktrecvd);

            IEncrypt enc = conn.enc;
            if (enc != null)
            {
                buffer.NumBytes = enc.Decrypt(p, buffer.Bytes, buffer.NumBytes);
            }

            if (buffer.NumBytes == 0)
            {
                // bad crc, or something
                _logManager.LogM(LogLevel.Malicious, nameof(Network), $"[pid={p.Id}] Failure decrypting packet.");
                buffer.Dispose();
                return;
            }

#if CFG_DUMP_RAW_PACKETS
            DumpPk($"RECV: about to process {buffer.NumBytes} bytes", buffer.Bytes.AsSpan(0, buffer.NumBytes));
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

        private void MainloopWork_CallPacketHandlers(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                ConnData conn = buffer.Conn;
                if (conn == null)
                    return;

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
            byte t1 = buffer.Bytes[0];

            if (t1 == 0x00)
            {
                byte t2 = buffer.Bytes[1];

                // 'core' packet
                if ((t2 < _oohandlers.Length) && (_oohandlers[t2] != null))
                {
                    _oohandlers[t2](buffer);
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
                    buffer.Dispose();
                }
            }
            else if (t1 < MAXTYPES)
            {
                _mainloop.QueueMainWorkItem(MainloopWork_CallPacketHandlers, buffer);
            }
            else
            {
                try
                {
                    if (conn.p != null)
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), $"[{conn.p.Name}] [pid={conn.p.Id}] Unknown packet type {t1}.");
                    else
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), $"(client connection) Unknown packet type {t1}.");
                }
                finally
                {
                    buffer.Dispose();
                }
            }
        }

        private static bool IsConnectionInitPacket(byte[] data)
        {
            return data != null
                && data.Length >= 2
                && data[0] == 0x00
                && ((data[1] == 0x01) || (data[1] == 0x11));
        }

        private void HandlePingPacketReceived(ListenData ld)
        {
            if (ld == null)
                return;

            IPEndPoint remoteIPEP = _objectPoolManager.IPEndPointPool.Get();

            try
            {
                Span<byte> data = stackalloc byte[8];
                int numBytes;
                EndPoint receivedFrom = remoteIPEP;

                try
                {
                    numBytes = ld.PingSocket.ReceiveFrom(data, SocketFlags.None, ref receivedFrom);
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

        private void HandleClientPacketReceived()
        {
            SubspaceBuffer buffer = _bufferPool.Get();
            IPEndPoint remoteIPEP = _objectPoolManager.IPEndPointPool.Get();

            try
            {
                EndPoint receivedFrom = remoteIPEP;

                try
                {
                    buffer.NumBytes = _clientSocket.ReceiveFrom(buffer.Bytes, buffer.Bytes.Length, SocketFlags.None, ref receivedFrom);
                }
                catch (SocketException ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"SocketException with error code {ex.ErrorCode} when receiving from client socket {_clientSocket.LocalEndPoint}. {ex}");
                    buffer.Dispose();
                    return;
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Network), $"Exception when receiving from client socket {_clientSocket.LocalEndPoint}. {ex}");
                    buffer.Dispose();
                    return;
                }

                if (buffer.NumBytes < 1)
                {
                    buffer.Dispose();
                    return;
                }

#if CFG_DUMP_RAW_PACKETS
                DumpPk($"RAW CLIENT DATA: {buffer.NumBytes} bytes", buffer.Bytes.AsSpan(0, buffer.NumBytes));
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

                    if (cc.Encryptor != null)
                    {
                        buffer.NumBytes = cc.Encryptor.Decrypt(cc, buffer.Bytes, buffer.NumBytes);

#if CFG_DUMP_RAW_PACKETS
                        DumpPk($"DECRYPTED CLIENT DATA: {buffer.NumBytes} bytes", new ReadOnlySpan<byte>(buffer.Bytes, 0, buffer.NumBytes));
#endif
                    }

                    if (buffer.NumBytes > 0)
                    {
                        buffer.Conn = conn;
                        conn.lastPkt = DateTime.UtcNow;
                        conn.bytesReceived += (ulong)buffer.NumBytes;
                        conn.pktReceived++;
                        Interlocked.Add(ref _globalStats.byterecvd, (ulong)buffer.NumBytes);
                        Interlocked.Increment(ref _globalStats.pktrecvd);

                        ProcessBuffer(buffer);
                    }
                    else
                    {
                        buffer.Dispose();
                        _logManager.LogM(LogLevel.Malicious, nameof(Network), "(client connection) Failed to decrypt packet.");
                    }

                    return;
                }

                buffer.Dispose();
                _logManager.LogM(LogLevel.Warn, nameof(Network), $"Got data on the client port that was not from any known connection ({receivedFrom}).");
            }
            finally
            {
                _objectPoolManager.IPEndPointPool.Return(remoteIPEP);
            }
        }

        private SubspaceBuffer BufferPacket(ConnData conn, ReadOnlySpan<byte> data, NetSendFlags flags, IReliableCallbackInvoker callbackInvoker = null)
        {
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
                    return null;
                }
                else
                {
                    if ((flags & NetSendFlags.Droppable) == NetSendFlags.Droppable)
                    {
                        conn.pktdropped++;
                        return null;
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
                    conn.cc.Handler.Connected();
                else if (conn.p != null)
                    _logManager.LogP(LogLevel.Malicious, nameof(Network), conn.p, "Got key response packet.");
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

            ref ReliableHeader rp = ref MemoryMarshal.AsRef<ReliableHeader>(buffer.Bytes);
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
                    _logManager.LogM(LogLevel.Drivel, nameof(Network), $"[{conn.p.Name}] [pid={conn.p.Id}] Reliable packet with too big delta ({sn} - {conn.c2sn}).");
                else
                    _logManager.LogM(LogLevel.Drivel, nameof(Network), $"(client connection) Reliable packet with too big delta ({sn} - {conn.c2sn}).");

                buffer.Dispose();
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
                    buffer.Dispose();
                }
                else
                {
                    conn.relbuf[spot] = buffer;
                    canProcess = (sn == conn.c2sn);
                }

                Monitor.Exit(conn.relmtx);

                // send the ack
                AckPacket ap = new(sn);

                lock (conn.olmtx)
                {
                    BufferPacket(
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

        private void ProcessAck(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            using (buffer)
            {
                if (buffer.NumBytes != 6) // ack packets are 6 bytes long
                    return;

                ConnData conn = buffer.Conn;
                if (conn == null)
                    return;

                ref AckPacket ack = ref MemoryMarshal.AsRef<AckPacket>(buffer.Bytes);
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
                            QueueMainloopWorkItem(b.CallbackInvoker, conn.p, true);
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
        }

        private void QueueMainloopWorkItem(IReliableCallbackInvoker callbackInvoker, Player player, bool success)
        {
            if (callbackInvoker == null)
                throw new ArgumentNullException(nameof(callbackInvoker));

            if (player == null)
                throw new ArgumentNullException(nameof(player));

            _mainloop.QueueMainWorkItem(
                MainloopWork_InvokeReliableCallback,
                new InvokeReliableCallbackDTO()
                {
                    CallbackInvoker = callbackInvoker,
                    Player = player,
                    Success = success,
                });
        }

        private struct InvokeReliableCallbackDTO
        {
            public IReliableCallbackInvoker CallbackInvoker;
            public Player Player;
            public bool Success;
        }

        private void MainloopWork_InvokeReliableCallback(InvokeReliableCallbackDTO dto)
        {
            while (dto.CallbackInvoker != null)
            {
                IReliableCallbackInvoker next = dto.CallbackInvoker.Next;

                using (dto.CallbackInvoker)
                {
                    if (dto.Player != null)
                    {
                        dto.CallbackInvoker.Invoke(dto.Player, dto.Success);
                    }
                }

                dto.CallbackInvoker = next;
            }
        }

        private void ProcessSyncRequest(SubspaceBuffer buffer)
        {
            if (buffer == null)
                return;

            try
            {
                if (buffer.NumBytes != TimeSyncRequest.Length)
                    return;

                ConnData conn = buffer.Conn;
                if (conn == null)
                    return;

                ref readonly TimeSyncRequest cts = ref MemoryMarshal.AsRef<TimeSyncRequest>(new ReadOnlySpan<byte>(buffer.Bytes, 0, buffer.NumBytes));
                uint clientTime = cts.Time;
                uint serverTime = ServerTick.Now;

                TimeSyncResponse ts = new();
                ts.Initialize(clientTime, serverTime);

                lock (conn.olmtx)
                {
                    // note: this bypasses bandwidth limits
                    SendRaw(conn, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref ts, 1)));

                    // submit data to lagdata
                    if (_lagCollect != null && conn.p != null)
                    {
                        TimeSyncData data = new()
                        {
                            ServerPacketsReceived = conn.pktReceived,
                            ServerPacketsSent = conn.pktSent,
                            ClientPacketsReceived = cts.PktRecvd,
                            ClientPacketsSent = cts.PktSent,
                            ServerTime = serverTime,
                            ClientTime = clientTime,
                        };

                        _lagCollect.TimeSync(conn.p, in data);
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
            public BigReceive BigReceive;
        }

        private void MainloopWork_CallBigPacketHandlers(BigPacketWork work)
        {
            try
            {
                if (work.ConnData == null || work.BigReceive == null || work.BigReceive.Buffer == null || work.BigReceive.Size < 1)
                    return;

                CallPacketHandlers(work.ConnData, work.BigReceive.Buffer, work.BigReceive.Size);
            }
            finally
            {
                if (work.BigReceive != null)
                {
                    // return the buffer to its pool
                    work.BigReceive.Dispose();
                }
            }
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
                    if (conn.BigRecv == null)
                    {
                        // Get a BigReceive object and give it to the ConnData object to 'own'.
                        conn.BigRecv = _bigReceivePool.Get();
                    }

                    int newSize = conn.BigRecv.Size + buffer.NumBytes - 2;

                    if (newSize <= 0 || newSize > Constants.MaxBigPacket)
                    {
                        if (conn.p != null)
                            _logManager.LogP(LogLevel.Malicious, nameof(Network), conn.p, $"Refusing to allocate {newSize} bytes (> {Constants.MaxBigPacket}).");
                        else
                            _logManager.LogM(LogLevel.Malicious, nameof(Network), $"(client connection) Refusing to allocate {newSize} bytes (> {Constants.MaxBigPacket}).");

                        conn.BigRecv.Dispose();
                        conn.BigRecv = null;
                        return;
                    }

                    // Append the data.
                    conn.BigRecv.Append(new(buffer.Bytes, 2, buffer.NumBytes - 2)); // data only, header removed

                    if (buffer.Bytes[1] == 0x08)
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
                        if (conn.p != null)
                            _logManager.LogP(LogLevel.Warn, nameof(Network), conn.p, $"Bad type for bigpacket: {conn.BigRecv.Buffer[0]}.");
                        else
                            _logManager.LogM(LogLevel.Warn, nameof(Network), $"(client connection) Bad type for bigpacket: {conn.BigRecv.Buffer[0]}.");

                        conn.BigRecv.Dispose();
                        conn.BigRecv = null;
                    }
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

                ref PresizedHeader header = ref MemoryMarshal.AsRef<PresizedHeader>(buffer.Bytes);
                int size = header.Size;
                Span<byte> data = buffer.Bytes.AsSpan(PresizedHeader.Length, buffer.NumBytes - PresizedHeader.Length);

                lock (conn.bigmtx)
                {
                    // only handle presized packets for player connections, not client connections
                    if (conn.p == null)
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
                    else if ((conn.sizedrecv.offset + buffer.NumBytes - 6) > size)
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

                // the client has requested a cancel for the sized transfer
                lock (conn.olmtx)
                {
                    // cancel current sized transfer
                    LinkedListNode<ISizedSendData> node = conn.sizedsends.First;
                    if (node != null)
                    {
                        ISizedSendData sd = node.Value;
                        if (sd != null)
                        {
                            sd.RequestData(0, Span<byte>.Empty); // notify transfer complete
                        }
                        conn.sizedsends.RemoveFirst();
                    }

                    ReadOnlySpan<byte> cancelPresizedAckSpan = stackalloc byte[2] { 0x00, 0x0C };
                    BufferPacket(conn, cancelPresizedAckSpan, NetSendFlags.Reliable);
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
            if (buffer == null || buffer.NumBytes < 2)
                return;

            try
            {
                Player p = buffer.Conn?.p;
                if (p == null)
                    return;

                int t2 = buffer.Bytes[1];

                if (t2 < _nethandlers.Length)
                {
                    _nethandlers[t2]?.Invoke(p, buffer.Bytes, buffer.NumBytes);
                }
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

            _connKey = _playerData.AllocatePlayerData<ConnData>();

            _config.DropTimeout = TimeSpan.FromMilliseconds(_configManager.GetInt(_configManager.Global, "Net", "DropTimeout", 3000) * 10);
            _config.MaxOutlistSize = _configManager.GetInt(_configManager.Global, "Net", "MaxOutlistSize", 500);

            // (deliberately) undocumented settings
            _config.MaxRetries = _configManager.GetInt(_configManager.Global, "Net", "MaxRetries", 15);
            _config.PresizedQueueThreshold = _configManager.GetInt(_configManager.Global, "Net", "PresizedQueueThreshold", 5);
            _config.PresizedQueuePackets = _configManager.GetInt(_configManager.Global, "Net", "PresizedQueuePackets", 25);
            _config.LimitReliableGroupingSize = _configManager.GetInt(_configManager.Global, "Net", "LimitReliableGroupingSize", 0) != 0;
            int reliableThreadCount = _configManager.GetInt(_configManager.Global, "Net", "ReliableThreads", 1);
            _config.PerPacketOverhead = _configManager.GetInt(_configManager.Global, "Net", "PerPacketOverhead", 28);
            _config.PingRefreshThreshold = TimeSpan.FromMilliseconds(10 * _configManager.GetInt(_configManager.Global, "Net", "PingDataRefreshTime", 200));
            _config.SimplePingPopulationMode = (PingPopulationMode)_configManager.GetInt(_configManager.Global, "Net", "SimplePingPopulationMode", 1);

            _bufferPool = objectPoolManager.GetPool<SubspaceBuffer>();
            _bigReceivePool = objectPoolManager.GetPool<BigReceive>();
            _reliableCallbackInvokerPool = objectPoolManager.GetPool<ReliableCallbackInvoker>();
            _bufferNodePool = new(new SubspaceBufferLinkedListNodePooledObjectPolicy());
            _objectPoolManager.TryAddTracked(_bufferNodePool);

            if (InitializeSockets() == false)
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

        #region IModuleLoaderAware Members

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

        #endregion

        #region INetworkEncryption Members

        private readonly List<ConnectionInitHandler> _connectionInitHandlers = new();
        private readonly ReaderWriterLockSlim _connectionInitLock = new(LockRecursionPolicy.NoRecursion);

        private bool ProcessConnectionInit(IPEndPoint remoteEndpoint, byte[] buffer, int len, ListenData ld)
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
            if (remoteEndpoint != null && _clienthash.TryGetValue(remoteEndpoint, out Player player))
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

                _clienthash[remoteEndpoint] = player;
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
                        BufferPacket(conn, bufferSpan, flags | NetSendFlags.Reliable);
                        position += Constants.ChunkSize;
                        len -= Constants.ChunkSize;
                    }

                    // Final packet is the 09 (signals the end of the big data)
                    // Note: Even if the data fit perfectly into the 08 packets (len == 0), we still need to send the 09 to mark the end.
                    bufferSpan[1] = 0x09;
                    data.Slice(position, len).CopyTo(bufferDataSpan);
                    BufferPacket(conn, bufferSpan.Slice(0, len + 2), flags | NetSendFlags.Reliable);
                }
            }
            else
            {
                lock (conn.olmtx)
                {
                    BufferPacket(conn, data, flags);
                }
            }
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
                BufferPacket(conn, data, NetSendFlags.Reliable, callbackInvoker);
            }
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

        void INetwork.AddPacket(C2SPacketType pktype, PacketDelegate func)
        {
            AddPacket((int)pktype, func);
        }

        private void AddPacket(int pktype, PacketDelegate func)
        {
            if (func == null)
                return;

            if (pktype >= 0 && pktype < MAXTYPES)
            {
                PacketDelegate d = _handlers[pktype];
                _handlers[pktype] = (d == null) ? func : (d += func);
            }
            else if ((pktype & 0xFF) == 0)
            {
                int b2 = pktype >> 8;

                if (b2 >= 0 && b2 < _nethandlers.Length && _nethandlers[b2] == null)
                {
                    _nethandlers[b2] = func;
                }
            }
        }

        void INetwork.RemovePacket(C2SPacketType pktype, PacketDelegate func)
        {
            RemovePacket((int)pktype, func);
        }

        private void RemovePacket(int pktype, PacketDelegate func)
        {
            if (func == null)
                return;

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

        void INetwork.AddSizedPacket(C2SPacketType pktype, SizedPacketDelegate func)
        {
            AddSizedPacket((int)pktype, func);
        }

        private void AddSizedPacket(int pktype, SizedPacketDelegate func)
        {
            if (func == null)
                return;

            if (pktype >= 0 && pktype < MAXTYPES)
            {
                SizedPacketDelegate d = _sizedhandlers[pktype];
                _sizedhandlers[pktype] = (d == null) ? func : (d += func);
            }
        }

        void INetwork.RemoveSizedPacket(C2SPacketType pktype, SizedPacketDelegate func)
        {
            RemoveSizedPacket((int)pktype, func);
        }

        private void RemoveSizedPacket(int pktype, SizedPacketDelegate func)
        {
            if (func == null)
                return;

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

        #endregion

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
                            sb.Append(d[pos].ToString("X2"));
                        }

                        for (; c < 16; c++)
                        {
                            sb.Append("   ");
                        }

                        sb.Append("  ");
                        sb.AppendLine(asciiBuilder.ToString());
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

        public void Dispose()
        {
            _stopCancellationTokenSource.Dispose();
            _clientConnectionsLock.Dispose();
        }

        private class SubspaceBufferLinkedListNodePooledObjectPolicy : PooledObjectPolicy<LinkedListNode<SubspaceBuffer>>
        {
            public override LinkedListNode<SubspaceBuffer> Create()
            {
                return new LinkedListNode<SubspaceBuffer>(null);
            }

            public override bool Return(LinkedListNode<SubspaceBuffer> obj)
            {
                if (obj == null)
                    return false;

                obj.List?.Remove(obj);
                obj.Value = null;

                return true;
            }
        }
    }
}

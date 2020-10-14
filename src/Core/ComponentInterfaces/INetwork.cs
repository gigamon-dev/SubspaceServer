using SS.Core.Packets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    [Flags]
    public enum NetSendFlags
    {
        /// <summary>
        /// Same as Unreliable
        /// </summary>
        None = 0x00,
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

        /// <summary>
        /// this if for use in the Network module only, do not use it directly
        /// </summary>
        Ack = 0x0100,
    }

    /// <summary>
    /// Delegate for a handler to an incoming regular packet.
    /// </summary>
    /// <param name="p">The player that sent the packet.</param>
    /// <param name="data">The buffer containing the packet data that was received.</param>
    /// <param name="length">Number of bytes in the data.</param>
    public delegate void PacketDelegate(Player p, byte[] data, int length);

    /// <summary>
    /// Delegate for a handler to an incoming sized packet (file transfer).
    /// </summary>
    /// <param name="p">The player that sent the packet.</param>
    /// <param name="data">The buffer containing the packet data that was received.</param>
    /// <param name="offset">
    /// Starting position of the data being transmitted.
    /// -1 indicates that the transfer was cancelled.
    /// </param>
    /// <param name="totalLength">
    /// Overall size of the transfer in bytes.
    /// -1 indicates that the transfer was cancelled.
    /// </param>
    public delegate void SizedPacketDelegate(Player p, Span<byte> data, int offset, int totalLength);

    /// <summary>
    /// Delegate for a callback when the send of a reliable packet completes sucessfully or unsuccessfully.
    /// </summary>
    /// <param name="p">The player the packet was being sent to.</param>
    /// <param name="success">Whether the packet was sucessfully sent.</param>
    public delegate void ReliableDelegate(Player p, bool success);

    /// <summary>
    /// Delegate for a callback when the send of a reliable packet completes sucessfully or unsuccessfully.
    /// The callback includes a parameter for state.
    /// </summary>
    /// <typeparam name="T">The type of state object.</typeparam>
    /// <param name="p">The player the packet was being sent to.</param>
    /// <param name="success">Whether the packet was sucessfully sent.</param>
    /// <param name="clos">The state object.</param>
    public delegate void ReliableDelegate<T>(Player p, bool success, T clos);

    /// <summary>
    /// Delegate for retrieving sized send data.
    /// This is used to request the sender to provide data for the transfer.
    /// </summary>
    /// <typeparam name="T">The type of the argument for passing state.</typeparam>
    /// <param name="clos">The state to pass (provides a way to identify the data to retrieve).</param>
    /// <param name="offset">The starting position of the data to retrieve.</param>
    /// <param name="dataSpan">
    /// The <see cref="Span{byte}"/> to fill with data. 
    /// <see cref="Span{T}.Empty"/> indicates the end of a transfer and can be used to perform any necessary cleanup.</param>
    public delegate void GetSizedSendDataDelegate<T>(T clos, int offset, Span<byte> dataSpan);

    public interface IReadOnlyNetStats
    {
        public ulong PingsReceived { get; }
        public ulong PacketsSent { get; }
        public ulong PacketsReceived { get; }
        public ulong BytesSent { get; }
        public ulong BytesReceived { get; }
        public ulong BuffersTotal { get; }
        public ulong BuffersUsed { get; }

        ReadOnlySpan<ulong> GroupedStats { get; }
        ReadOnlySpan<ulong> PriorityStats { get; }
    }

    public class NetClientStats
    {
        /// <summary>
        /// Server to client sequence number
        /// </summary>
        public int s2cn;

        /// <summary>
        /// Client to server sequence number
        /// </summary>
        public int c2sn;

        public uint PacketsSent;
        public uint PacketsReceived;
        public ulong BytesSent;
        public ulong BytesReceived;

        /// <summary>
        /// Server to client packets dropped
        /// </summary>
        public ulong PacketsDropped;

        public string EncryptionName;

        public IPEndPoint IPEndPoint;

        //TODO: public byte[] BandwidthLimitInfo;
    }

    public interface INetwork : IComponentInterface
    {
        /// <summary>
        /// To send data to a single player.
        /// </summary>
        /// <param name="p">player to send to</param>
        /// <param name="data">data to send</param>
        /// <param name="len">length of data to send</param>
        /// <param name="flags">flags specifying options for the send</param>
        void SendToOne(Player p, byte[] data, int len, NetSendFlags flags);

        /// <summary>
        /// To send data to a single player.
        /// </summary>
        /// <param name="p">player to send to</param>
        /// <param name="data">data to send</param>
        /// <param name="flags">flags specifying options for the send</param>
        void SendToOne(Player p, Span<byte> data, NetSendFlags flags);

        /// <summary>
        /// To send data to players in a specific arena or
        /// To send data to players in all arenas.
        /// A specified person can be excluded from the send.
        /// </summary>
        /// <param name="arena">arena to send data to, null for all arenas</param>
        /// <param name="except">player to exclude from the send</param>
        /// <param name="data">data to send</param>
        /// <param name="len">length of data to send</param>
        /// <param name="flags">flags specifying options for the send</param>
        void SendToArena(Arena arena, Player except, byte[] data, int len, NetSendFlags flags);

        /// <summary>
        /// To send data to a set of players.
        /// </summary>
        /// <param name="set">players to send to</param>
        /// <param name="data">data to send</param>
        /// <param name="len">length of data to send</param>
        /// <param name="flags">flags specifying options for the send</param>
        void SendToSet(IEnumerable<Player> set, byte[] data, int len, NetSendFlags flags);

        /// <summary>
        /// To send data to a set of players.
        /// </summary>
        /// <param name="set">The players to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flag(s) specifying options for the send.</param>
        void SendToSet(IEnumerable<Player> set, Span<byte> data, NetSendFlags flags);

        /// <summary>
        /// To send data to a target of players
        /// </summary>
        /// <param name="target">target describing what players to send data to</param>
        /// <param name="data">array containing the data</param>
        /// <param name="len">the length of the data</param>
        /// <param name="flags">flags specifying options for the send</param>
        void SendToTarget(ITarget target, byte[] data, int len, NetSendFlags flags);

        /// <summary>
        /// To send data to a player and receive a callback after the data has been sent.
        /// </summary>
        /// <param name="p">player sending data to</param>
        /// <param name="data">array conaining the data</param>
        /// <param name="len">number of bytes to send</param>
        /// <param name="callback">the callback which will be called after the data has been sent</param>
        void SendWithCallback(Player p, byte[] data, int len, ReliableDelegate callback);

        /// <summary>
        /// To send data to a player and receive a callback after the data has been sent.
        /// </summary>
        /// <param name="p">player sending data to</param>
        /// <param name="data">array conaining the data</param>
        /// <param name="len">number of bytes to send</param>
        /// <param name="callback">the callback which will be called after the data has been sent</param>
        /// <param name="clos">argument to use when calling the callback</param>
        void SendWithCallback<T>(Player p, byte[] data, int len, ReliableDelegate<T> callback, T clos);

        /// <summary>
        /// To send sized data to a player.
        /// Used for sending files to players such as map/news/updates.
        /// </summary>
        /// <typeparam name="T">The type of the argument used in the callback to retrieve data to send.</typeparam>
        /// <param name="p">The player to send data to.</param>
        /// <param name="len">The total number of bytes to send in the transfer.</param>
        /// <param name="requestData">The delegate to call back for retrieving pieces of data for the transfer.</param>
        /// <param name="clos">The argument to pass when calling <paramref name="requestData"/>.</param>
        /// <returns></returns>
        bool SendSized<T>(Player p, int len, GetSizedSendDataDelegate<T> requestData, T clos);

        /// <summary>
        /// To register a handler for a regular packet.
        /// <remarks>
        /// This is usually used to register handlers for game packets.
        /// Note, this can also be used to register a handler for 'core' network level packets.  
        /// However, registering 'core' handlers doesn't appear to be used in asss.</remarks>
        /// </summary>
        /// <param name="pktype">The type of packet to register.</param>
        /// <param name="func">The handler to call when a packet is received.</param>
        void AddPacket(C2SPacketType pktype, PacketDelegate func);

        /// <summary>
        /// To unregister a handler for a given packet type.
        /// </summary>
        /// <param name="pktype"></param>
        /// <param name="func"></param>
        void RemovePacket(C2SPacketType pktype, PacketDelegate func);

        /// <summary>
        /// To register a handler for a sized packet.
        /// <remarks>This is used for receiving file uploads.  Includes voices (wave messages in macros).</remarks>
        /// </summary>
        /// <param name="pktype"></param>
        /// <param name="func"></param>
        void AddSizedPacket(C2SPacketType pktype, SizedPacketDelegate func);

        /// <summary>
        /// To unregister a handler for a sized packet.
        /// </summary>
        /// <param name="pktype"></param>
        /// <param name="func"></param>
        void RemoveSizedPacket(C2SPacketType pktype, SizedPacketDelegate func);

        /// <summary>
        /// Gets statistics about the Network module.
        /// </summary>
        /// <returns>The stats.</returns>
        IReadOnlyNetStats GetStats();

        NetClientStats GetClientStats(Player p);

        /// <summary>
        /// Collection of information about sockets that the Network module is listening on.
        /// Do not modify data in any of these lists.
        /// </summary>
        /// <remarks>The network module only modifies the list when it Loads or Unloads.  So for the most part, reading should be thread-safe.</remarks>
        IReadOnlyList<ListenData> Listening { get; }
    }
}

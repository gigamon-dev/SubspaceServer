using SS.Core.Packets;
using System;
using System.Collections.Generic;
using System.Net;

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
        Droppable = 0x02,
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
    public delegate void SizedPacketDelegate(Player p, ReadOnlySpan<byte> data, int offset, int totalLength);

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
    /// The buffer to fill with data. 
    /// An empty span indicates the end of a transfer and can be used to perform any necessary cleanup.
    /// </param>
    public delegate void GetSizedSendDataDelegate<T>(T clos, int offset, Span<byte> dataSpan);

    public interface IReadOnlyNetStats
    {
        ulong PingsReceived { get; }
        ulong PacketsSent { get; }
        ulong PacketsReceived { get; }
        ulong BytesSent { get; }
        ulong BytesReceived { get; }
        ulong BuffersTotal { get; }
        ulong BuffersUsed { get; }

        ulong GroupedStats0 { get; }
        ulong GroupedStats1 { get; }
        ulong GroupedStats2 { get; }
        ulong GroupedStats3 { get; }
        ulong GroupedStats4 { get; }
        ulong GroupedStats5 { get; }
        ulong GroupedStats6 { get; }
        ulong GroupedStats7 { get; }

        ulong PriorityStats0 { get; }
        ulong PriorityStats1 { get; }
        ulong PriorityStats2 { get; }
        ulong PriorityStats3 { get; }
        ulong PriorityStats4 { get; }
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
        /// Sends data to a single player.
        /// </summary>
        /// <param name="p">The player to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags specifying options for the send.</param>
        void SendToOne(Player p, ReadOnlySpan<byte> data, NetSendFlags flags);

        /// <summary>
        /// Sends data to a single player.
        /// </summary>
        /// <typeparam name="TData">The type of data packet to send.</typeparam>
        /// <param name="p">The player to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags specifying options for the send.</param>
        void SendToOne<TData>(Player p, ref TData data, NetSendFlags flags) where TData : struct;

        /// <summary>
        /// Sends data to players in a specific arena or all arenas,
        /// with the ability to exclude a specified player from the send.
        /// </summary>
        /// <param name="arena">The arena to send data to, or <see langword="null"/> for all arenas.</param>
        /// <param name="except">The player to exclude from the send.  <see langword="null"/> for no exclusion.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags specifying options for the send.</param>
        void SendToArena(Arena arena, Player except, ReadOnlySpan<byte> data, NetSendFlags flags);

        /// <summary>
        /// Sends data to players in a specific arena or all arenas,
        /// with the ability to exclude a specified player from the send.
        /// </summary>
        /// <typeparam name="TData">The type of data packet to send.</typeparam>
        /// <param name="arena">The arena to send data to, or <see langword="null"/> for all arenas.</param>
        /// <param name="except">The player to exclude from the send.  <see langword="null"/> for no exclusion.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags specifying options for the send.</param>
        void SendToArena<TData>(Arena arena, Player except, ref TData data, NetSendFlags flags) where TData : struct;

        /// <summary>
        /// To send data to a set of players.
        /// </summary>
        /// <param name="set">The players to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flag(s) specifying options for the send.</param>
        void SendToSet(HashSet<Player> set, ReadOnlySpan<byte> data, NetSendFlags flags);

        /// <summary>
        /// To send data to a set of players.
        /// </summary>
        /// <typeparam name="TData">The type of data packet to send.</typeparam>
        /// <param name="set">The players to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flag(s) specifying options for the send.</param>
        void SendToSet<TData>(HashSet<Player> set, ref TData data, NetSendFlags flags) where TData : struct;

        /// <summary>
        /// Sends data to a target of players
        /// </summary>
        /// <param name="target">The target describing which players to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags specifying options for the send.</param>
        void SendToTarget(ITarget target, ReadOnlySpan<byte> data, NetSendFlags flags);

        /// <summary>
        /// Sends data to a target of players
        /// </summary>
        /// <typeparam name="TData">The type of data packet to send.</typeparam>
        /// <param name="target">The target describing which players to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags specifying options for the send.</param>
        void SendToTarget<TData>(ITarget target, ref TData data, NetSendFlags flags) where TData : struct;

        /// <summary>
        /// Reliably sends data to a player and invokes a callback after the data has been sent.
        /// </summary>
        /// <param name="p">The player to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="callback">The callback to invoke after the data has been sent.</param>
        void SendWithCallback(Player p, ReadOnlySpan<byte> data, ReliableDelegate callback);

        /// <summary>
        /// Reliably sends data to a player and invokes a callback after the data has been sent.
        /// </summary>
        /// <typeparam name="TData">The type of data packet to send.</typeparam>
        /// <param name="p">The player to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="callback">The callback to invoke after the data has been sent.</param>
        void SendWithCallback<TData>(Player p, ref TData data, ReliableDelegate callback) where TData : struct;

        /// <summary>
        /// Reliably sends data to a player and invokes a callback after the data has been sent.
        /// </summary>
        /// <typeparam name="TState">The type of argument used in the callback.</typeparam>
        /// <param name="p">The player to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="callback">The callback to invoke after the data has been sent.</param>
        /// <param name="clos">The state to send when invoking the callback.</param>
        void SendWithCallback<TState>(Player p, ReadOnlySpan<byte> data, ReliableDelegate<TState> callback, TState clos);

        /// <summary>
        /// Reliably sends data to a player and invokes a callback after the data has been sent.
        /// </summary>
        /// <typeparam name="TData">The type of data packet to send.</typeparam>
        /// <typeparam name="TState">The type of argument used in the callback.</typeparam>
        /// <param name="p">The player to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="callback">The callback to invoke after the data has been sent.</param>
        /// <param name="clos">The state to send when invoking the callback.</param>
        void SendWithCallback<TData, TState>(Player p, ref TData data, ReliableDelegate<TState> callback, TState clos) where TData : struct;

        /// <summary>
        /// Sends 'sized' data to a player.
        /// Used for sending files to players (including, but not limited to: map files (lvl and lvz), news.txt, and client updates).
        /// </summary>
        /// <typeparam name="T">The type of the argument used in the callback to retrieve data to send.</typeparam>
        /// <param name="p">The player to send data to.</param>
        /// <param name="len">The total number of bytes to send in the transfer.</param>
        /// <param name="requestData">The delegate to call back for retrieving pieces of data for the transfer.</param>
        /// <param name="clos">The argument to pass when calling <paramref name="requestData"/>.</param>
        /// <returns></returns>
        bool SendSized<T>(Player p, int len, GetSizedSendDataDelegate<T> requestData, T clos);

        /// <summary>
        /// Registers a handler for a regular packet type.
        /// </summary>
        /// <remarks>
        /// This is usually used to register handlers for game packets.
        /// Note, this can also be used to register a handler for 0x00 0x13 by passing a packet type of 0x1300.
        /// </remarks>
        /// <param name="pktype">The type of packet to register.</param>
        /// <param name="func">The handler to register.</param>
        void AddPacket(C2SPacketType pktype, PacketDelegate func);

        /// <summary>
        /// Unregisters a handler for a regular packet type.
        /// </summary>
        /// <param name="pktype">The type of packet to unregister.</param>
        /// <param name="func">The handler to unregister.</param>
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

        /// <summary>
        /// Gets network information for a player.
        /// </summary>
        /// <param name="p">The player to get information about.</param>
        /// <returns>The information.</returns>
        NetClientStats GetClientStats(Player p);

        /// <summary>
        /// Gets the endpoint (IP Address and port) and connectAs that the server is listening on for game clients.
        /// </summary>
        /// <param name="index">Index of the data to get.</param>
        /// <param name="endPoint">The endpoint being listened to.</param>
        /// <param name="connectAs">The base arena name the listening endpoint is configured with.</param>
        /// <returns>True if data was found for the given index.  Otheriwse, false.</returns>
        bool TryGetListenData(int index, out IPEndPoint endPoint, out string connectAs);

        /// <summary>
        /// Gets population statistics for the arenas associated with a specified connectAs.
        /// </summary>
        /// <param name="connectAs">The base arena name to get stats for.</param>
        /// <param name="total">The total # of players.</param>
        /// <param name="playing">The # of players in ships (not spectating).</param>
        /// <returns>True if stats were found for the specified <paramref name="connectAs"/>. Otherwise, false.</returns>
        bool TryGetPopulationStats(string connectAs, out uint total, out uint playing);

        /// <summary>
        /// Collection of information about sockets that the Network module is listening on.
        /// Do not modify data in any of these lists.
        /// </summary>
        /// <remarks>The network module only modifies the list when it Loads or Unloads.  So for the most part, reading should be thread-safe.</remarks>
        IReadOnlyList<ListenData> Listening { get; }
    }
}

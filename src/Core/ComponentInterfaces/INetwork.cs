﻿using SS.Packets.Game;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Flags that indicate how data is to be sent.
    /// </summary>
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
    /// Flags that indicate how data was received.
    /// </summary>
    [Flags]
    public enum NetReceiveFlags
    {
        /// <summary>
        /// No flags.
        /// </summary>
        None = 0x00,

        /// <summary>
        /// Whether the data was received in a reliable packet (00 03).
        /// </summary>
        Reliable = 0x01,

        /// <summary>
        /// Whether the data was received in a grouped packet (00 0E).
        /// </summary>
        Grouped = 0x02,

        /// <summary>
        /// Whether the data was received in a "big" packet (00 08, 00 09).
        /// </summary>
        Big = 0x04,

        /// <summary>
        /// Whether the data was received in a "sized" packet (00 0A).
        /// </summary>
        //Sized = 0x08, // sized has its own delegate, so don't need to include this here
    }

	/// <summary>
	/// Delegate for a handler to an incoming regular packet.
	/// </summary>
	/// <param name="player">The player that sent the packet.</param>
	/// <param name="data">
	/// The buffer containing the packet data that was received.
	/// <para>
	/// The buffer is guaranteed to be at least <see cref="Constants.MaxPacket"/>, 
    /// which can be larger than the <paramref name="length"/> of bytes received.
	/// This means a handler can use <see cref="System.Runtime.InteropServices.MemoryMarshal.AsRef{T}(Span{byte})"/>
	/// with a structure larger than the <paramref name="length"/> of bytes received.
	/// Doing so allows variable length data to be processed with a struct that contains all fields, including the optional ones.
	/// It is up to the handler to know which fields are available based on the <paramref name="length"/>.
	/// For example, packets with Continuum-only fields added at the end, 
	/// or position packets where the extra position data at the end is optional.
	/// </para>
	/// </param>
	/// <param name="length">Number of bytes in <paramref name="data"/> that were received.</param>
	/// <param name="flags">Flags indicating how the data was received.</param>
	public delegate void PacketDelegate(Player player, Span<byte> data, int length, NetReceiveFlags flags);

    /// <summary>
    /// Delegate for a handler to an incoming sized packet (file transfer).
    /// </summary>
    /// <param name="player">The player that sent the packet.</param>
    /// <param name="data">The buffer containing the packet data that was received.</param>
    /// <param name="offset">
    /// Starting position of the data being transmitted.
    /// -1 indicates that the transfer was cancelled.
    /// </param>
    /// <param name="totalLength">
    /// Overall size of the transfer in bytes.
    /// -1 indicates that the transfer was cancelled.
    /// </param>
    public delegate void SizedPacketDelegate(Player player, ReadOnlySpan<byte> data, int offset, int totalLength);

    /// <summary>
    /// Delegate for a callback when the send of a reliable packet completes successfully or unsuccessfully.
    /// </summary>
    /// <param name="player">The player the packet was being sent to.</param>
    /// <param name="success">Whether the packet was sucessfully sent.</param>
    public delegate void ReliableDelegate(Player player, bool success);

    /// <summary>
    /// Delegate for a callback when the send of a reliable packet completes successfully or unsuccessfully.
    /// The callback includes a parameter for state.
    /// </summary>
    /// <typeparam name="T">The type of state object.</typeparam>
    /// <param name="player">The player the packet was being sent to.</param>
    /// <param name="success">Whether the packet was sucessfully sent.</param>
    /// <param name="clos">The state object.</param>
    public delegate void ReliableDelegate<T>(Player player, bool success, T clos);

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

        ulong RelGroupedStats0 { get; }
        ulong RelGroupedStats1 { get; }
        ulong RelGroupedStats2 { get; }
        ulong RelGroupedStats3 { get; }
        ulong RelGroupedStats4 { get; }
        ulong RelGroupedStats5 { get; }
        ulong RelGroupedStats6 { get; }
        ulong RelGroupedStats7 { get; }

        ulong PriorityStats0 { get; }
        ulong PriorityStats1 { get; }
        ulong PriorityStats2 { get; }
        ulong PriorityStats3 { get; }
        ulong PriorityStats4 { get; }
    }

    public struct NetClientStats
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

        public StringBuilder BandwidthLimitInfo { get; init; }
    }

    public interface INetwork : IComponentInterface
    {
        /// <summary>
        /// Sends data to a single player.
        /// </summary>
        /// <param name="player">The player to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags specifying options for the send.</param>
        void SendToOne(Player player, ReadOnlySpan<byte> data, NetSendFlags flags);

        /// <summary>
        /// Sends data to a single player.
        /// </summary>
        /// <typeparam name="TData">The type of data packet to send.</typeparam>
        /// <param name="player">The player to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="flags">Flags specifying options for the send.</param>
        void SendToOne<TData>(Player player, ref TData data, NetSendFlags flags) where TData : struct;

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
        /// <param name="player">The player to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="callback">The callback to invoke after the data has been sent.</param>
        void SendWithCallback(Player player, ReadOnlySpan<byte> data, ReliableDelegate callback);

        /// <summary>
        /// Reliably sends data to a player and invokes a callback after the data has been sent.
        /// </summary>
        /// <typeparam name="TData">The type of data packet to send.</typeparam>
        /// <param name="player">The player to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="callback">The callback to invoke after the data has been sent.</param>
        void SendWithCallback<TData>(Player player, ref TData data, ReliableDelegate callback) where TData : struct;

        /// <summary>
        /// Reliably sends data to a player and invokes a callback after the data has been sent.
        /// </summary>
        /// <typeparam name="TState">The type of argument used in the callback.</typeparam>
        /// <param name="player">The player to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="callback">The callback to invoke after the data has been sent.</param>
        /// <param name="clos">The state to send when invoking the callback.</param>
        void SendWithCallback<TState>(Player player, ReadOnlySpan<byte> data, ReliableDelegate<TState> callback, TState clos);

        /// <summary>
        /// Reliably sends data to a player and invokes a callback after the data has been sent.
        /// </summary>
        /// <typeparam name="TData">The type of data packet to send.</typeparam>
        /// <typeparam name="TState">The type of argument used in the callback.</typeparam>
        /// <param name="player">The player to send data to.</param>
        /// <param name="data">The data to send.</param>
        /// <param name="callback">The callback to invoke after the data has been sent.</param>
        /// <param name="clos">The state to send when invoking the callback.</param>
        void SendWithCallback<TData, TState>(Player player, ref TData data, ReliableDelegate<TState> callback, TState clos) where TData : struct;

        /// <summary>
        /// Queues up a job to send 'sized' data to a player.
        /// Used for sending files to players (including, but not limited to: map files (lvl and lvz), news.txt, and client updates).
        /// </summary>
        /// <typeparam name="T">The type of the argument used in the callback to retrieve data to send.</typeparam>
        /// <param name="player">The player to send data to.</param>
        /// <param name="len">The total number of bytes to send in the transfer.</param>
        /// <param name="requestData">The delegate to call back for retrieving pieces of data for the transfer.</param>
        /// <param name="clos">The argument to pass when calling <paramref name="requestData"/>.</param>
        /// <returns><see langword="true"/> if the job was queued. Otherwise, <see langword="false"/>.</returns>
        bool SendSized<T>(Player player, int len, GetSizedSendDataDelegate<T> requestData, T clos);

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
        /// <param name="player">The player to get information about.</param>
        /// <param name="stats">The information to populate.</param>
        void GetClientStats(Player player, ref NetClientStats stats);

        /// <summary>
        /// Gets how long it has been since a packet was received from a specified player.
        /// </summary>
        /// <param name="player">The player to check.</param>
        /// <returns>The <see cref="TimeSpan"/> since the last packet was received.</returns>
        TimeSpan GetLastPacketTimeSpan(Player player);

        /// <summary>
        /// Gets the endpoint (IP Address and port) and connectAs that the server is listening on for game clients.
        /// </summary>
        /// <param name="index">Index of the data to get.</param>
        /// <param name="endPoint">The endpoint being listened to.</param>
        /// <param name="connectAs">The base arena name the listening endpoint is configured with.</param>
        /// <returns><see langword="true"/> if data was found for the given index.  Otheriwse, <see langword="false"/>.</returns>
        bool TryGetListenData(int index, out IPEndPoint endPoint, out string connectAs);

        /// <summary>
        /// Gets population statistics for the arenas associated with a specified connectAs.
        /// </summary>
        /// <param name="connectAs">The base arena name to get stats for.</param>
        /// <param name="total">The total # of players.</param>
        /// <param name="playing">The # of players in ships (not spectating).</param>
        /// <returns><see langword="true"/> if stats were found for the specified <paramref name="connectAs"/>. Otherwise, <see langword="false"/>.</returns>
        bool TryGetPopulationStats(string connectAs, out uint total, out uint playing);

        /// <summary>
        /// Collection of information about sockets that the Network module is listening on.
        /// Do not modify data in any of these lists.
        /// </summary>
        /// <remarks>The network module only modifies the list when it Loads or Unloads.  So for the most part, reading should be thread-safe.</remarks>
        IReadOnlyList<ListenData> Listening { get; }
    }
}

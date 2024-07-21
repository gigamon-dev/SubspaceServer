using System.Diagnostics;
using System.Text;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Represents priority of network traffic.
    /// </summary>
    public enum BandwidthPriority
    {
        UnreliableLow = 0,
        Unreliable,
        UnreliableHigh,
        Reliable,
        Ack,
    }

    /// <summary>
    /// Interface for a service that assists in limiting network bandwidth.
    /// </summary>
    public interface IBandwidthLimiter
    {
        /// <summary>
        /// Refreshes the current state of how many much bandwidth is available to be used for each priority.
        /// </summary>
        /// <param name="asOf">The <see cref="Stopwatch"/> timestamp to recalculate stats as of.</param>
        void Iter(long asOf);

        /// <summary>
        /// Checks if <paramref name="bytes"/> bytes at priority <paramref name="priority"/> can be sent.
        /// If they can be sent, modifies stats and returns true, otherwise returns false.
        /// </summary>
        /// <param name="bytes">The number of bytes of the data.</param>
        /// <param name="priority">The priority of the data.</param>
        /// <returns>True if the data should be sent. Otherwise, false.</returns>
        bool Check(int bytes, BandwidthPriority priority);

        /// <summary>
        /// Adjusts stats for when an ACK is received.
        /// </summary>
        void AdjustForAck();

        /// <summary>
        /// Adjusts stats for when a reliable packet is resent.
        /// </summary>
        void AdjustForRetry();

        /// <summary>
        /// Gets the max # of reliable packets that can be sent at the same time without being ACK'd yet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// E.g., if X is the lowest pending (not yet sent OR sent but not yet acknolwedged) reliable sequence # for a connection, 
        /// then only allow sending of reliable packets with a sequence # less than or equal to:
        /// X + <see cref="GetSendWindowSize"/>.
        /// </para>
        /// </remarks>
        /// <returns>The window size.</returns>
        int GetSendWindowSize();

        /// <summary>
        /// Gets info about the limiter.
        /// </summary>
        /// <param name="sb">The <see cref="StringBuilder"/> to populate with info.</param>
        void GetInfo(StringBuilder sb);
    }

    /// <summary>
    /// Interface for a service that provides <see cref="IBandwidthLimiter"/>s.
    /// </summary>
    public interface IBandwidthLimiterProvider : IComponentInterface
    {
        /// <summary>
        /// Gets a bandwidth limiter for a single connection.
        /// </summary>
        /// <returns>A bandwidth limiter.</returns>
        IBandwidthLimiter New();

        /// <summary>
        /// Frees a bandwidth limiter.
        /// </summary>
        /// <param name="limiter">The limiter to free.</param>
        void Free(IBandwidthLimiter limiter);
    }
}

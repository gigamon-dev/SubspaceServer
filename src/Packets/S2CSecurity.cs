using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SS.Core.Packets
{
    /// <summary>
    /// Packet that the server sends to either:
    /// <list type="bullet">
    /// <item>synchronize a client when the player enters an arena</item>
    /// <item>request a client respond with a <see cref="C2SSecurity"/> (<see cref="C2SPacketType.SecurityResponse"/>)</item>
    /// </list>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2CSecurity
    {
        /// <summary>
        /// 0x18
        /// </summary>
        public byte Type;

        /// <summary>
        /// Seed for greens.
        /// </summary>
        public uint GreenSeed;

        /// <summary>
        /// Seed for doors.
        /// </summary>
        public uint DoorSeed;

        /// <summary>
        /// Timestamp
        /// </summary>
        public uint Timestamp;

        /// <summary>
        /// Key for checksum use.
        /// <para>
        /// 0 when just syncing a client up (when a player enters an arena).
        /// </para>
        /// <para>
        /// Non-zero for requesting that the client respond to a security check.
        /// </para>
        /// </summary>
        public uint Key;

        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        public const int Length = 17;
    }
}

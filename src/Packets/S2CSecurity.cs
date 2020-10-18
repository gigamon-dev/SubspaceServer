using System;
using System.Buffers.Binary;
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
        private uint greenSeed;
        private uint doorSeed;
        private uint timestamp;
        private uint key;

        /// <summary>
        /// Seed for greens.
        /// </summary>
        public uint GreenSeed
        {
            get { return BitConverter.IsLittleEndian ? greenSeed : BinaryPrimitives.ReverseEndianness(greenSeed); }
            set { greenSeed = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        /// <summary>
        /// Seed for doors.
        /// </summary>
        public uint DoorSeed
        {
            get { return BitConverter.IsLittleEndian ? doorSeed : BinaryPrimitives.ReverseEndianness(doorSeed); }
            set { doorSeed = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        /// <summary>
        /// Timestamp
        /// </summary>
        public uint Timestamp
        {
            get { return BitConverter.IsLittleEndian ? timestamp : BinaryPrimitives.ReverseEndianness(timestamp); }
            set { timestamp = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        /// <summary>
        /// Key for checksum use.
        /// <para>
        /// 0 when just syncing a client up (when a player enters an arena).
        /// </para>
        /// <para>
        /// Non-zero for requesting that the client respond to a security check.
        /// </para>
        /// </summary>
        public uint Key
        {
            get { return BitConverter.IsLittleEndian ? key : BinaryPrimitives.ReverseEndianness(key); }
            set { key = BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        }

        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        public const int Length = 17;
    }
}

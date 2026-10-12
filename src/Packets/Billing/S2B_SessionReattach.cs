using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    /// <summary>
    /// Asks the billing server to give back the sessions of players who were logged in through it
    /// when the connection was lost. Followed by <see cref="Count"/> <see cref="S2B_SessionReattachEntry"/>.
    /// </summary>
    /// <remarks>
    /// Only sent to a billing server that said it supports it, with <see cref="B2S_BillerCapabilities"/>.
    /// Answered by a <see cref="B2S_SessionReattachResponseHeader"/> with the same <see cref="Sequence"/>.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2B_SessionReattachHeader(ushort sequence, byte count)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2B_SessionReattachHeader>();

        #endregion

        public readonly byte Type = (byte)S2BPacketType.SessionReattach;
        private readonly ushort sequence = LittleEndianConverter.Convert(sequence);
        public readonly byte Count = count;

        #region Helper Properties

        /// <summary>
        /// Identifies the packet among those sent since connecting. Starts at 0 for each connection.
        /// </summary>
        public ushort Sequence => LittleEndianConverter.Convert(sequence);

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2B_SessionReattachEntry(int connectionId, uint userId, ReadOnlySpan<byte> name)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2B_SessionReattachEntry>();

        #endregion

        private readonly int connectionId = LittleEndianConverter.Convert(connectionId);
        private readonly uint userId = LittleEndianConverter.Convert(userId);
        public readonly NameInlineArray Name = new(name);

        #region Helper Properties

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        /// <summary>
        /// The id the billing server gave for the player when they logged in, <see cref="B2S_UserLogin.UserId"/>.
        /// </summary>
        public uint UserId => LittleEndianConverter.Convert(userId);

        #endregion

        #region Inline Array Types

        [InlineArray(Length)]
        public struct NameInlineArray
        {
            public const int Length = 32;

            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;

            public NameInlineArray(ReadOnlySpan<byte> value)
            {
                value = value.SliceNullTerminated();
                if (value.Length > Length - 1)
                    value = value[..(Length - 1)];

                value.CopyTo(this);
                this[value.Length..].Clear();
            }
        }

        #endregion
    }

    /// <summary>
    /// Tells the billing server that the zone is done asking for sessions back, so that it can close
    /// those the zone did not ask for.
    /// </summary>
    /// <remarks>
    /// Sent once per connection, after the last <see cref="B2S_SessionReattachResponseHeader"/>, and
    /// also when there were no sessions to ask for.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2B_SessionReattachEnd(ushort packetCount, uint playerCount)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2B_SessionReattachEnd>();

        #endregion

        public readonly byte Type = (byte)S2BPacketType.SessionReattachEnd;
        private readonly ushort packetCount = LittleEndianConverter.Convert(packetCount);
        private readonly uint playerCount = LittleEndianConverter.Convert(playerCount);

        #region Helper Properties

        /// <summary>
        /// How many <see cref="S2BPacketType.SessionReattach"/> packets were sent.
        /// </summary>
        public ushort PacketCount => LittleEndianConverter.Convert(packetCount);

        /// <summary>
        /// How many <see cref="S2B_SessionReattachEntry"/> were sent, over all of the packets.
        /// </summary>
        public uint PlayerCount => LittleEndianConverter.Convert(playerCount);

        #endregion
    }
}

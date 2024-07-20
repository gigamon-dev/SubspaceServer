using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct B2S_UserChannelChat
    {
        #region Static Members

        /// <summary>
        /// The maximum # of bytes the text portion of the packet can be.
        /// </summary>
        public const int MaxTextBytes = ChatPacket.MaxMessageBytes;

        /// <summary>
        /// The length of the header (excludes the variable length text) in bytes.
        /// </summary>
        public static readonly int HeaderLength;

        /// <summary>
        /// The minimum packet length (empty text, only containing a null-terminator) in bytes.
        /// </summary>
		public static readonly int MinLength;

        /// <summary>
        /// The maximum packet length (maxed out text) in bytes.
        /// </summary>
        public static readonly int MaxLength;

        static B2S_UserChannelChat()
        {
			HeaderLength = Marshal.SizeOf<B2S_UserChannelChat>();
            MinLength = HeaderLength + 1;
            MaxLength = HeaderLength + MaxTextBytes;
        }

		/// <summary>
		/// Gets the text bytes of a packet.
		/// </summary>
		/// <param name="packetBytes">The full packet to get the text bytes of.</param>
		/// <returns>A slice of <paramref name="packetBytes"/> containing the text bytes.</returns>
		public static Span<byte> GetTextBytes(Span<byte> packetBytes)
        {
            if (packetBytes.Length <= HeaderLength)
                return Span<byte>.Empty;

			packetBytes = packetBytes[HeaderLength..];
			if (packetBytes.Length > MaxTextBytes)
				packetBytes = packetBytes[..MaxTextBytes];

            return packetBytes;
		}

        #endregion

        public readonly byte Type;
        private readonly int connectionId;
        public readonly byte Channel;
        // Followed by the text bytes which must be null-terminated.

        #region Helper Properties

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        #endregion
    }
}

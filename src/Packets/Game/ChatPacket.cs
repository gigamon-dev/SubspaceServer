using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
	/// <summary>
	/// Struct for a chat packet, both C2S and S2C.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Subspace appears to use a subset of the Windows-1252 encoding for chat messages.
	/// </para>
	/// <para>
	/// The characters the Continuum client is able to display include:
	/// <list type="bullet">
	/// <item><term>[0x20 - 0x7E]</term>The displayable ASCII characters.</item>
	/// <item><term>[0xC0 - 0x0FF], excluding × (0xD7) and ÷ (0xF7)</term>Some extended characters from ISO-8859-1 which Windows-1252 also includes. Does not include the multiplication and division symbols.</item>
	/// <item><term>€ (0x80), Š (0x8A), Ž (0x8E), š (0x9A), ž (0x9E), and Ÿ (0x9F)</term>Characters from ISO-8859-15, but as Windows-1252 codes.</item>
	/// </list>
	/// </para>
	/// <para>
	/// A chat packet should be limited to 255 bytes.
	/// The header is 5 bytes. Leaving 250 bytes for the message, with the last byte being a null terminator.
	/// So the message is limited to 249 characters (which appears to be the limit that the continuum client has).
	/// </para>
	/// <para>
	/// I'm guessing that the 255 limit is so that a maxed out chat packet can fit inside a 0x0E grouped packet.
	/// However, if it's being sent reliably, then there's a 6 byte overhead for the reliable header.
	/// So, as long as a reliable chat packet isn't larger than 249 bytes (not maxed out), it can be added into a grouped packet.
	/// Though it's fine in the opposite direction, a reliable packet containing a grouped packet, can contain a maxed out chat packet.
	/// </para>
	/// </remarks>
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChatPacket
    {
		#region Static Members

		/// <summary>
		/// The minimum # of bytes for a chat packet. Header + 1 byte (null-terminator) for the message.
		/// </summary>
		public static readonly int MinLength;

        /// <summary>
        /// The maximum # of bytes for a chat packet. Header + maximum message byte length.
        /// </summary>
        public static readonly int MaxLength;

        /// <summary>
        /// Number of bytes in the header (non-message portion) of a <see cref="ChatPacket"/>
        /// </summary>
        public static readonly int HeaderLength;

        /// <summary>
        /// The maximum # of characters that can be produced by decoding the message portion of a <see cref="ChatPacket"/>.
        /// </summary>
        public static readonly int MaxMessageChars = StringUtils.DefaultEncoding.GetMaxCharCount(MaxMessageBytes - 1); // -1 to exclude the null-terminator

        /// <summary>
        /// The maximum # of bytes for the message portion of a <see cref="ChatPacket"/>.
        /// </summary>
        public const int MaxMessageBytes = 250;

        static ChatPacket()
        {
			HeaderLength = Marshal.SizeOf<ChatPacket>();
			MaxLength = HeaderLength + MaxMessageBytes;
            MinLength = HeaderLength + 1;
        }

		/// <summary>
		/// Gets the message bytes of a chat packet by slicing it, including removing the null-terminator.
		/// </summary>
		/// <param name="packetBytes">The full chat packet to get the message bytes of.</param>
		/// <returns>A slice of <paramref name="packetBytes"/> containing the message bytes.</returns>
		public static Span<byte> GetMessageBytes(Span<byte> packetBytes)
		{
			if (packetBytes.Length <= HeaderLength)
				return Span<byte>.Empty;

			packetBytes = packetBytes[HeaderLength..];
			if (packetBytes.Length > MaxMessageBytes)
				packetBytes = packetBytes[..MaxMessageBytes];

			return packetBytes.SliceNullTerminated();
		}

		/// <summary>
		/// Gets the # of bytes a chat packet needs to be to store a particular message.
		/// </summary>
		/// <remarks>
		/// This method will not return a size greater than <see cref="MaxLength"/>.
		/// In other words, if the <paramref name="message"/> is too large, 
		/// it will be truncated when calling <see cref="SetMessage(Span{byte}, ReadOnlySpan{char})"/>.
		/// </remarks>
		/// <param name="message">The message to get the packet size for.</param>
		/// <returns>The # of bytes required.</returns>
		public static int GetPacketByteCount(ReadOnlySpan<char> message)
		{
			return int.Min(
				MaxLength,
				HeaderLength + StringUtils.DefaultEncoding.GetByteCount(message) + 1); // +1 for null-terminator
		}

		/// <summary>
		/// Writes the message portion of a chat packet.
		/// </summary>
		/// <remarks>
		/// The <paramref name="message"/> is truncated if it does not fit.
		/// </remarks>
		/// <param name="packetBytes">The bytes of the entire chat packet.</param>
		/// <param name="message">The message to write into the chat packet.</param>
		/// <returns>The resulting length (bytes) of the chat packet.</returns>
		public static int SetMessage(Span<byte> packetBytes, ReadOnlySpan<char> message)
		{
			packetBytes = packetBytes[HeaderLength..];
			return HeaderLength + packetBytes.WriteNullTerminatedString(message.TruncateForEncodedByteLimit(packetBytes.Length));
		}

		#endregion

        public byte Type;
        public byte ChatType;
        public byte Sound;
        private short playerId;
		// Followed by the message bytes which must be null-terminated.

		public ChatPacket(byte type, byte chatType, byte sound, short playerId)
		{
			if (type != (byte)S2CPacketType.Chat && type != (byte)C2SPacketType.Chat)
				throw new ArgumentOutOfRangeException(nameof(type), "Invalid type of chat packet.");

			Type = type;
			ChatType = chatType;
			Sound = sound;
			PlayerId = playerId;
		}

		#region Helpers

		public short PlayerId
        {
			readonly get { return LittleEndianConverter.Convert(playerId); }
            set { playerId = LittleEndianConverter.Convert(value); }
        }

		#endregion
    }
}


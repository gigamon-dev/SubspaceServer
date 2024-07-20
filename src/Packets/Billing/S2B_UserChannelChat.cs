using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2B_UserChannelChat(int connectionId)
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

		static S2B_UserChannelChat()
        {
			HeaderLength = Marshal.SizeOf<S2B_UserChannelChat>();
			MinLength = HeaderLength + 1;
			MaxLength = HeaderLength + MaxTextBytes;
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
		public static int SetText(Span<byte> packetBytes, ReadOnlySpan<char> message)
		{
			packetBytes = packetBytes[HeaderLength..];
			if (packetBytes.Length > MaxTextBytes)
				packetBytes = packetBytes[..MaxTextBytes];

			return HeaderLength + packetBytes.WriteNullTerminatedString(message.TruncateForEncodedByteLimit(packetBytes.Length - 1));
		}

		#endregion

		public readonly byte Type = (byte)S2BPacketType.UserChannelChat;
        private readonly int connectionId = LittleEndianConverter.Convert(connectionId);
        public readonly ChannelInlineArray Channel;
		// Followed by the text bytes which must be null-terminated.

		#region Helper Properties

		public int ConnectionId => LittleEndianConverter.Convert(connectionId);

		#endregion

		#region Inline Array Types

		[InlineArray(Length)]
		public struct ChannelInlineArray
		{
			public const int Length = 32;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public void Set(ReadOnlySpan<char> value)
			{
				StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length - 1));
			}
		}

		#endregion
	}
}

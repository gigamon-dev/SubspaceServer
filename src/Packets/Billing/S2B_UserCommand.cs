using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2B_UserCommand
    {
		#region Static members

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

		/// <summary>
		/// The maximum # of characters that can be encoded or decoded from the text portion.
		/// </summary>
		public static readonly int MaxTextChars;

        static S2B_UserCommand()
        {
			HeaderLength = Marshal.SizeOf<S2B_UserCommand>();
			MinLength = HeaderLength + 1;
			MaxLength = HeaderLength + MaxTextBytes;
			MaxTextChars = StringUtils.DefaultEncoding.GetMaxCharCount(MaxTextBytes - 1); // -1 for the null-terminator
        }

		/// <summary>
		/// Writes the text portion of the packet.
		/// </summary>
		/// <param name="packetBytes">The bytes of the entire packet.</param>
		/// <param name="value">The text to write into the packet.</param>
		/// <param name="addCommandChar">Whether the '?' command character should be prepended.</param>
		/// <returns>The resulting length (bytes) of the packet.</returns>
		public static int SetText(Span<byte> packetBytes, ReadOnlySpan<char> value, bool addCommandChar)
		{
			packetBytes = packetBytes[HeaderLength..];
			if (packetBytes.Length > MaxTextBytes)
				packetBytes = packetBytes[..MaxTextBytes];

			int bytesWritten = 0;

			if (addCommandChar)
			{
				bytesWritten = StringUtils.DefaultEncoding.GetBytes("?", packetBytes);
				packetBytes = packetBytes[bytesWritten..];
			}

			bytesWritten += packetBytes.WriteNullTerminatedString(value.TruncateForEncodedByteLimit(packetBytes.Length - 1));
			return HeaderLength + bytesWritten;
		}

		#endregion

		public readonly byte Type;
        private int connectionId;
		// Followed by the text bytes which must be null-terminated.

		public S2B_UserCommand(int connectionId)
        {
            Type = (byte)S2BPacketType.UserCommand;
            this.connectionId = LittleEndianConverter.Convert(connectionId);
        }
    }
}

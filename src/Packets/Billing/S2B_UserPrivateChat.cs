using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2B_UserPrivateChat
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

        static S2B_UserPrivateChat()
        {
			HeaderLength = Marshal.SizeOf<S2B_UserPrivateChat>();
			MinLength = HeaderLength + 1;
			MaxLength = HeaderLength + MaxTextBytes;
			MaxTextChars = StringUtils.DefaultEncoding.GetMaxCharCount(MaxTextBytes - 1); // -1 for the null-terminator
        }

		public static int SetText(Span<byte> packetBytes, ReadOnlySpan<char> value)
		{
			packetBytes = packetBytes[HeaderLength..];
			if (packetBytes.Length > MaxTextBytes)
				packetBytes = packetBytes[..MaxTextBytes];

			return HeaderLength + packetBytes.WriteNullTerminatedString(value.TruncateForEncodedByteLimit(packetBytes.Length - 1));
		}

		#endregion

		public readonly byte Type;
        private int connectionId;
        private uint groupId;
        public byte SubType;
        public byte Sound;
		// Followed by the text bytes which must be null-terminated.

		public S2B_UserPrivateChat(int connectionId, uint groupId, byte subType, byte sound)
        {
            Type = (byte)S2BPacketType.UserPrivateChat;
            this.connectionId = LittleEndianConverter.Convert(connectionId);
            this.groupId = LittleEndianConverter.Convert(groupId);
            SubType = subType;
            Sound = sound;
        }
    }
}

using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    /// <summary>
    /// Chat packet
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
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct ChatPacket
    {
        /// <summary>
        /// Number of bytes in the header (non-message portion) of a <see cref="ChatPacket"/>
        /// </summary>
        public const int HeaderLength = 5;

        /// <summary>
        /// The minimum # of bytes for a chat packet. Header + 1 byte (null-terminator) for the message.
        /// </summary>
        public const int MinLength = HeaderLength + 1;

        /// <summary>
        /// The maximum # of bytes for a chat packet. Header + maximum message byte length.
        /// </summary>
        public const int MaxLength = HeaderLength + MessageBytesLength;

        public byte Type;
        public byte ChatType;
        public byte Sound;

        private short playerId;
        public short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
            set { playerId = LittleEndianConverter.Convert(value); }
        }

        private const int MessageBytesLength = 250;
        private fixed byte messageBytes[MessageBytesLength];
        public Span<byte> MessageBytes => MemoryMarshal.CreateSpan(ref messageBytes[0], MessageBytesLength);

        /// <summary>
        /// Writes encoded bytes of a string into a the message bytes.
        /// </summary>
        /// <param name="str">The string to write.</param>
        /// <returns>The number of bytes written.</returns>
        public int SetMessage(ReadOnlySpan<char> str)
        {
            return MessageBytes.WriteNullTerminatedString(str.TruncateForEncodedByteLimit(MessageBytesLength - 1));
        }
    }
}


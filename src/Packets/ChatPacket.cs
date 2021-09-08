using SS.Utilities;
using System;
using System.Runtime.CompilerServices;
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
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct ChatPacket
    {
        /// <summary>
        /// Number of bytes in the header (non-message portion) of a <see cref="ChatPacket"/>
        /// </summary>
        public const int HeaderLength = 5;

        /// <summary>
        /// A chat packet should be limited to 255 bytes.
        /// The header is 5 bytes. Leaving 250 for the message, with the last byte being a null terminator.
        /// So the message is limited to 249 characters (which appears to be the limit that the continuum client has).
        /// <para>
        /// I'm guessing that the 255 limit is so that a maxed out chat packet can fit inside a 0x0E grouped packet.
        /// However, if it's being sent reliably, then there's a 6 byte overhead for the reliable header.
        /// So, as long as a reliable chat packet isn't larger than 249 bytes (not maxed out), it can be added into a grouped packet.
        /// </para>
        /// </summary>
        public const int MaxMessageLength = MessageLength - 1; // -1 for the null terminating byte

        public byte Type;
        public byte ChatType;
        public byte Sound;

        private short playerId;
        public short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
            set { playerId = LittleEndianConverter.Convert(value); }
        }

        private const int MessageLength = 250;
        private fixed byte messageBytes[MessageLength];
        public Span<byte> MessageBytes => new(Unsafe.AsPointer(ref messageBytes[0]), MessageLength);
    }
}


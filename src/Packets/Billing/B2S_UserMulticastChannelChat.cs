using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct B2S_UserMulticastChannelChat
    {
        public static readonly int MinLength;
        public static readonly int MaxLength;
        public static readonly int LengthWithoutRecipientsOrText;

        static B2S_UserMulticastChannelChat()
        {
            MaxLength = Marshal.SizeOf<B2S_UserMulticastChannelChat>();
            LengthWithoutRecipientsOrText = MaxLength - dataBytesLength;
            MinLength = LengthWithoutRecipientsOrText + MChannelChatRecipient.Length + 1; // 1 recipient, with 1 null-terminator for the text
        }

        public byte Type;
        public byte Count;

        // Recipients are repated Count times. Followed by the text.
        private const int dataBytesLength = 1024; // unsure of the actual maximum
        private fixed byte dataBytes[dataBytesLength];
        public Span<byte> DataBytes => MemoryMarshal.CreateSpan(ref dataBytes[0], dataBytesLength);

        /// <summary>
        /// Gets the recipients portion of the packet.
        /// </summary>
        /// <param name="packetLength">The length of the entire packet.</param>
        /// <returns>The recipients, or <see cref="Span{MChannelChatRecipient}.Empty"/> if there is a problem.</returns>
        public ReadOnlySpan<MChannelChatRecipient> GetRecipients(int packetLength)
        {
            if (!IsLengthValid(packetLength))
                return Span<MChannelChatRecipient>.Empty;

            return MemoryMarshal.Cast<byte, MChannelChatRecipient>(DataBytes.Slice(0, Count * MChannelChatRecipient.Length));
        }

        /// <summary>
        /// Gets the text portion of the packet.
        /// </summary>
        /// <param name="packetLength">The length of the entire packet.</param>
        /// <returns>The bytes of the text, or <see cref="Span{byte}.Empty"/> if there is a problem.</returns>
        public Span<byte> GetTextBytes(int packetLength)
        {
            if (!IsLengthValid(packetLength))
                return Span<byte>.Empty;

            int recipientBytes = Count * MChannelChatRecipient.Length;
            int textLength = packetLength - (LengthWithoutRecipientsOrText + recipientBytes);
            if (textLength > 250)
                textLength = 250;

            return DataBytes.Slice(Count * MChannelChatRecipient.Length, textLength);
        }

        /// <summary>
        /// Checks whether a length is valid for the # of recipients.
        /// </summary>
        /// <param name="packetLength">The length of the entire packet.</param>
        /// <returns>True if the length is valid, otherwise false.</returns>
        public bool IsLengthValid(int packetLength)
        {
            int recipientBytes = Count * MChannelChatRecipient.Length;
            return recipientBytes < dataBytesLength
                && packetLength - (LengthWithoutRecipientsOrText + recipientBytes) > 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MChannelChatRecipient
    {
        public static readonly int Length;

        static MChannelChatRecipient()
        {
            Length = Marshal.SizeOf<MChannelChatRecipient>();
        }

        private int connectionId;
        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        public byte Channel;
    }
}

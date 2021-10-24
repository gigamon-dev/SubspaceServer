using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2B_ServerCapabilities
    {
        public readonly byte Type;
        private uint bitField;
        private uint BitField
        {
            get => LittleEndianConverter.Convert(bitField);
            set => bitField = LittleEndianConverter.Convert(value);
        }

        private const uint MultiCastChatMask = 0b00000001;
        private const uint SupportDemographicsMask = 0b00000010;

        public bool MultiCastChat
        {
            get => (BitField & MultiCastChatMask) != 0;
            set
            {
                if (value)
                    BitField |= SupportDemographicsMask;
                else
                    BitField &= ~SupportDemographicsMask;
            }
        }

        public bool SupportDemographics
        {
            get => (BitField & SupportDemographicsMask) != 0;
            set
            {
                if (value)
                    BitField |= SupportDemographicsMask;
                else
                    BitField &= ~SupportDemographicsMask;
            }
        }

        public S2B_ServerCapabilities(bool multiCastChat, bool supportDemographics)
        {
            Type = (byte)S2BPacketType.ServerCapabilities;
            bitField = 0;
            MultiCastChat = multiCastChat;
            SupportDemographics = supportDemographics;
        }
    }
}

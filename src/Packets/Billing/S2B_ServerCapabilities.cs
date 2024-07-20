using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2B_ServerCapabilities
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2B_ServerCapabilities>();

        #endregion

        public readonly byte Type;
        private readonly uint bitField;

        public S2B_ServerCapabilities(bool multiCastChat, bool supportDemographics)
        {
            Type = (byte)S2BPacketType.ServerCapabilities;
            bitField = 0;
            MultiCastChat = multiCastChat;
            SupportDemographics = supportDemographics;
        }

        #region Helper Properties

        private uint BitField
        {
            readonly get => LittleEndianConverter.Convert(bitField);
            init => bitField = LittleEndianConverter.Convert(value);
        }

        private const uint MultiCastChatMask = 0b00000001;
        private const uint SupportDemographicsMask = 0b00000010;

        public bool MultiCastChat
        {
			readonly get => (BitField & MultiCastChatMask) != 0;
            init
            {
                if (value)
                    BitField |= SupportDemographicsMask;
                else
                    BitField &= ~SupportDemographicsMask;
            }
        }

        public bool SupportDemographics
        {
			readonly get => (BitField & SupportDemographicsMask) != 0;
            init
            {
                if (value)
                    BitField |= SupportDemographicsMask;
                else
                    BitField &= ~SupportDemographicsMask;
            }
        }

        #endregion
    }
}

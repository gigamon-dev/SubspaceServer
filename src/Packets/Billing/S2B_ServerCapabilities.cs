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

        public S2B_ServerCapabilities(bool multiCastChat, bool supportDemographics, bool supportsBanEnforcement)
        {
            Type = (byte)S2BPacketType.ServerCapabilities;
            bitField = 0;
            MultiCastChat = multiCastChat;
            SupportDemographics = supportDemographics;
            SupportsBanEnforcement = supportsBanEnforcement;
        }

        #region Helper Properties

        private uint BitField
        {
            readonly get => LittleEndianConverter.Convert(bitField);
            init => bitField = LittleEndianConverter.Convert(value);
        }

        private const uint MultiCastChatMask = 0b00000001;
        private const uint SupportDemographicsMask = 0b00000010;
        private const uint SupportsBanEnforcementMask = 0b00000100;

        public bool MultiCastChat
        {
            readonly get => (BitField & MultiCastChatMask) != 0;
            init
            {
                if (value)
                    BitField |= MultiCastChatMask;
                else
                    BitField &= ~MultiCastChatMask;
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

        /// <summary>
        /// Whether the zone enforces the restriction bans that the billing server delegates to it
        /// with <see cref="B2S_PlayerRestrictions"/>.
        /// </summary>
        /// <remarks>
        /// This is an extension that not every billing server knows about. One that doesn't simply
        /// never sends <see cref="B2SPacketType.PlayerRestrictions"/>, so advertising it is safe.
        /// </remarks>
        public bool SupportsBanEnforcement
        {
            readonly get => (BitField & SupportsBanEnforcementMask) != 0;
            init
            {
                if (value)
                    BitField |= SupportsBanEnforcementMask;
                else
                    BitField &= ~SupportsBanEnforcementMask;
            }
        }

        #endregion
    }
}

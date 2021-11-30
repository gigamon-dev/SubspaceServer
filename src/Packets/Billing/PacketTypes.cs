namespace SS.Packets.Billing
{
    public enum S2BPacketType : byte
    {
        Ping = 0x01,
        ServerConnect = 0x02,
        ServerDisconnect = 0x03,
        UserLogin = 0x04,
        UserLogoff = 0x05,
        UserPrivateChat = 0x07,
        UserDemographics = 0x0D,
        UserBanner = 0x10,
        UserScore = 0x11,
        UserCommand = 0x13,
        UserChannelChat = 0x14,
        ServerCapabilities = 0x15,
    }

    public enum B2SPacketType : byte
    {
        UserLogin = 0x01,
        UserPrivateChat = 0x03,
        UserKickout = 0x08,
        UserCommandChat = 0x09,
        UserChannelChat = 0x0A,
        ScoreReset = 0x31,
        UserPacket = 0x32,
        BillingIdentity = 0x33,
        UserMulticastChannelChat = 0x34,
    }
}

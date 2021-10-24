namespace SS.Core.Packets.Billing
{
    public enum S2BPacketType : byte
    {
        Ping = 1,
        ServerConnect = 2,
        ServerDisconnect = 3,
        UserLogin = 4,
        UserLogoff = 5,
        UserPrivateChat = 7,
        UserDemographics = 13,
        UserBanner = 16,
        UserScore = 17,
        UserCommand = 19,
        UserChannelChat = 20,
        ServerCapabilities = 21,
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
        UserMchannelChat = 0x34,
    }
}

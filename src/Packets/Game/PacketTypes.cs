namespace SS.Packets.Game
{
    public enum S2CPacketType : byte
    {
        Zero = 0x00,
        WhoAmI = 0x01,
        EnteringArena = 0x02,
        PlayerEntering = 0x03,
        PlayerLeaving = 0x04,
        Weapon = 0x05,
        Kill = 0x06,
        Chat = 0x07,
        Green = 0x08,
        ScoreUpdate = 0x09, 
        LoginResponse = 0x0A, 
        SoccerGoal = 0x0B, 
        Voice = 0x0C, 
        FreqChange = 0x0D, 
        Turret = 0x0E, 
        Settings = 0x0F, 
        IncomingFile = 0x10,
        // subspace client does no operation with 0x11
        FlagLoc = 0x12, 
        FlagPickup = 0x13, 
        FlagReset = 0x14, 
        TurretKickoff = 0x15, 
        FlagDrop = 0x16,
        // subspace client does no operation with 0x17
        Security = 0x18, 
        RequestForFile = 0x19,
        TimedGame = 0x1A,
        /// <summary>
        /// just 1 byte, tells client they need to reset their ship
        /// </summary>
        ShipReset = 0x1B, 
        /// <summary>
        /// two bytes, if byte two is true, client needs to send their item info in
        /// position packets, OR
        /// three bytes, parameter is the player id of a player going into spectator mode
        /// </summary>
        SpecData = 0x1C, 
        ShipChange = 0x1D, 
        BannerToggle = 0x1E, 
        Banner = 0x1F, 
        PrizeRecv = 0x20, 
        Brick = 0x21, 
        TurfFlags = 0x22, 
        PeriodicReward = 0x23, 
        /// <summary>
        /// Complex speed stats
        /// </summary>
        Speed = 0x24, 
        /// <summary>
        /// two bytes, if byte two is true, you can use UFO if you want to
        /// </summary>
        Ufo = 0x25,
        // subspace client does no operation with 0x26
        KeepAlive = 0x27, 
        Position = 0x28, 
        MapFilename = 0x29, 
        MapData = 0x2A, 
        SetCrownTimer = 0x2B, 
        Crown = 0x2C,
        AddCrownTimer = 0x2D,
        Ball = 0x2E, 
        Arena = 0x2F, 
        /// <summary>
        /// vie's old method of showing ads
        /// </summary>
        AdBanner = 0x30,
        /// <summary>
        /// vie sent it after a good login, only with billing
        /// </summary>
        LoginOK = 0x31, 
        /// <summary>
        /// u8 type - ui16 x tile coords - ui16 y tile coords
        /// </summary>
        WarpTo = 0x32, 
        LoginText = 0x33, 
        ContVersion = 0x34, 
        /// <summary>
        /// u8 type - unlimited number of ui16 with obj id (if & 0xF000, means turning off)
        /// </summary>
        ToggleObj = 0x35, 
        MoveObj = 0x36, 
        /// <summary>
        /// two bytes, if byte two is true, client should send damage info
        /// </summary>
        ToggleDamage = 0x37, 
        /// <summary>
        /// complex, the info used from a *watchdamage
        /// </summary>
        Damage = 0x38,
        // missing 39 3A
        Redirect = 0x3B, 
    }

    public enum C2SPacketType
    {
        GotoArena = 0x01, 
        LeaveArena = 0x02, 
        Position = 0x03, 
        // missing 04 : appears to be disabled in subgame
        Die = 0x05, 
        Chat  = 0x06, 
        Green = 0x07, 
        SpecRequest = 0x08, 
        Login = 0x09, 
        Rebroadcast = 0x0A, 
        UpdateRequest = 0x0B, 
        MapRequest = 0x0C, 
        NewsRequest = 0x0D, 
        RelayVoice = 0x0E, 
        SetFreq = 0x0F, 
        AttachTo = 0x10, 
        // missing 12 : appears to be disabled in subgame
        TouchFlag = 0x13, 
        TurretKickOff = 0x14, 
        DropFlags = 0x15,
        /// <summary>
        /// uploading a file to server
        /// </summary>
        UploadFile = 0x16, 
        RegData = 0x17, 
        SetShip = 0x18, 
        /// <summary>
        /// sending new banner
        /// </summary>
        Banner = 0x19, 
        SecurityResponse = 0x1A, 
        ChecksumIsMatch = 0x1B, 
        Brick = 0x1C, 
        SettingChange = 0x1D, 
        CrownExpired = 0x1E, 
        ShootBall = 0x1F, 
        PickupBall = 0x20, 
        Goal = 0x21, 
        // missing 22 : subspace client sends extra checksums and other security stuff
        // missing 23
        ContLogin = 0x24, 
        Damage = 0x32
    }
}

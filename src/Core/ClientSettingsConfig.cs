using SS.Core.ComponentInterfaces;

namespace SS.Core
{
    /// <summary>
    /// This provides helper arrays that assist the <see cref="SS.Core.Modules.ClientSettings"/> module to loading client settings.
    /// </summary>
    /// <remarks>
    /// This is equivalent to clientset.def in ASSS.
    /// </remarks>
    public static class ClientSettingsConfig
    {
        [ConfigHelp<int>("Bullet", "BulletDamageLevel", ConfigScope.Arena, Description = "Maximum amount of damage that a L1 bullet will cause")]
        [ConfigHelp<int>("Bomb", "BombDamageLevel", ConfigScope.Arena, Description = "Amount of damage a bomb causes at its center point (for all bomb levels)")]
        [ConfigHelp<int>("Bullet", "BulletAliveTime", ConfigScope.Arena, Description = "How long bullets live before disappearing (in ticks)")]
        [ConfigHelp<int>("Bomb", "BombAliveTime", ConfigScope.Arena, Description = "Time bomb is alive (in ticks)")]
        [ConfigHelp<int>("Misc", "DecoyAliveTime", ConfigScope.Arena, Description = "Time a decoy is alive (in ticks)")]
        [ConfigHelp<int>("Misc", "SafetyLimit", ConfigScope.Arena, Description = "Amount of time that can be spent in the safe zone (in ticks)")]
        [ConfigHelp<int>("Misc", "FrequencyShift", ConfigScope.Arena, Description = "Amount of random frequency shift applied to sounds in the game")]
        [ConfigHelp<int>("Team", "MaxFrequency", ConfigScope.Arena, Description = "Maximum number of frequencies allowed in arena (5 would allow frequencies 0,1,2,3,4)")]
        [ConfigHelp<int>("Repel", "RepelSpeed", ConfigScope.Arena, Description = "Speed at which players are repelled")]
        [ConfigHelp<int>("Mine", "MineAliveTime", ConfigScope.Arena, Description = "Time that mines are active (in ticks)")]
        [ConfigHelp<int>("Burst", "BurstDamageLevel", ConfigScope.Arena, Description = "Maximum amount of damage caused by a single burst bullet")]
        [ConfigHelp<int>("Bullet", "BulletDamageUpgrade", ConfigScope.Arena, Description = "Amount of extra damage each bullet level will cause")]
        [ConfigHelp<int>("Flag", "FlagDropDelay", ConfigScope.Arena, Description = "Time before flag is dropped by carrier (0=never)")]
        [ConfigHelp<int>("Flag", "EnterGameFlaggingDelay", ConfigScope.Arena, Description = "Time a new player must wait before they are allowed to see flags")]
        [ConfigHelp<int>("Rocket", "RocketThrust", ConfigScope.Arena, Description = "Thrust value given while a rocket is active")]
        [ConfigHelp<int>("Rocket", "RocketSpeed", ConfigScope.Arena, Description = "Speed value given while a rocket is active")]
        [ConfigHelp<int>("Shrapnel", "InactiveShrapDamage", ConfigScope.Arena, Description = "Amount of damage shrapnel causes in it's first 1/4 second of life")]
        [ConfigHelp<int>("Wormhole", "SwitchTime", ConfigScope.Arena, Description = "How often the wormhole switches its destination")]
        [ConfigHelp<int>("Misc", "ActivateAppShutdownTime", ConfigScope.Arena, Description = "Amount of time a ship is shutdown after application is reactivated")]
        [ConfigHelp<int>("Shrapnel", "ShrapnelSpeed", ConfigScope.Arena, Description = "Speed that shrapnel travels")]
        public static readonly (string Section, string Key)[] LongNames =
        [
            ("Bullet", "BulletDamageLevel"), /* * 1000 */
	        ("Bomb", "BombDamageLevel"), /* * 1000 */
	        ("Bullet", "BulletAliveTime"),
            ("Bomb", "BombAliveTime"),
            ("Misc", "DecoyAliveTime"),
            ("Misc", "SafetyLimit"),
            ("Misc", "FrequencyShift"),
            ("Team", "MaxFrequency"),
            ("Repel", "RepelSpeed"),
            ("Mine", "MineAliveTime"),
            ("Burst", "BurstDamageLevel"), /* * 1000 */
	        ("Bullet", "BulletDamageUpgrade"), /* * 1000 */
	        ("Flag", "FlagDropDelay"),
            ("Flag", "EnterGameFlaggingDelay"),
            ("Rocket", "RocketThrust"),
            ("Rocket", "RocketSpeed"),
            ("Shrapnel", "InactiveShrapDamage"), /* * 1000 */
	        ("Wormhole", "SwitchTime"),
            ("Misc", "ActivateAppShutdownTime"),
            ("Shrapnel", "ShrapnelSpeed"),
        ];

        [ConfigHelp<short>("Latency", "SendRoutePercent", ConfigScope.Arena, Description = "Percentage of the ping time that is spent on the C2S portion of the ping (used in more accurately syncronizing clocks)")]
        [ConfigHelp<short>("Bomb", "BombExplodeDelay", ConfigScope.Arena, Description = "How long after the proximity sensor is triggered before bomb explodes")]
        [ConfigHelp<short>("Misc", "SendPositionDelay", ConfigScope.Arena, Min = 1, Description = "Amount of time between position packets sent by client")]
        [ConfigHelp<short>("Bomb", "BombExplodePixels", ConfigScope.Arena, Description = "Blast radius in pixels for an L1 bomb (L2 bombs double this, L3 bombs triple this)")]
        [ConfigHelp<short>("Prize", "DeathPrizeTime", ConfigScope.Arena, Description = "How long the prize exists that appears after killing somebody")]
        [ConfigHelp<short>("Bomb", "JitterTime", ConfigScope.Arena, Description = "How long the screen jitters from a bomb hit (in ticks)")]
        [ConfigHelp<short>("Kill", "EnterDelay", ConfigScope.Arena, Description = "How long after a player dies before he can re-enter the game (in ticks)")]
        [ConfigHelp<short>("Prize", "EngineShutdownTime", ConfigScope.Arena, Description = "Time the player is affected by an 'Engine Shutdown' Prize (in ticks)")]
        [ConfigHelp<short>("Bomb", "ProximityDistance", ConfigScope.Arena, Description = "Radius of proximity trigger in tiles (each bomb level adds 1 to this amount)")]
        [ConfigHelp<short>("Kill", "BountyIncreaseForKill", ConfigScope.Arena, Description = "Number of points added to players bounty each time he kills an opponent")]
        [ConfigHelp<short>("Misc", "BounceFactor", ConfigScope.Arena, Description = "How bouncy the walls are (16 = no speed loss)")]
        [ConfigHelp<short>("Radar", "MapZoomFactor", ConfigScope.Arena, Min = 1, Description = "A number representing how much the map is zoomed out for radar. (48 = whole map on radar, 49+ = effectively disable radar)")]
        [ConfigHelp<short>("Kill", "MaxBonus", ConfigScope.Arena, Description = "This is if you have flags, can add more points per a kill.")]
        [ConfigHelp<short>("Kill", "MaxPenalty", ConfigScope.Arena, Description = "This is if you have flags, can take away points per a kill.")]
        [ConfigHelp<short>("Kill", "RewardBase", ConfigScope.Arena, Description = "This is shown added to a person's bty, but isn't added from points for a kill.")]
        [ConfigHelp<short>("Repel", "RepelTime", ConfigScope.Arena, Description = "Time players are affected by the repel (in ticks)")]
        [ConfigHelp<short>("Repel", "RepelDistance", ConfigScope.Arena, Description = "Number of pixels from the player that are affected by a repel")]
        [ConfigHelp<short>("Misc", "TickerDelay", ConfigScope.Arena, Description = "Amount of time between ticker help messages")]
        [ConfigHelp<bool>("Flag", "FlaggerOnRadar", ConfigScope.Arena, Description = "Whether the flaggers appear on radar in red")]
        [ConfigHelp<short>("Flag", "FlaggerKillMultiplier", ConfigScope.Arena, Description = "Number of times more points are given to a flagger (1 = double points, 2 = triple points)")]
        [ConfigHelp<short>("Prize", "PrizeFactor", ConfigScope.Arena, Description = "Number of prizes hidden is based on number of players in game. This number adjusts the formula, higher numbers mean more prizes. (Note: 10000 is max, 10 greens per person)")]
        [ConfigHelp<short>("Prize", "PrizeDelay", ConfigScope.Arena, Description = "How often prizes are regenerated (in ticks)")]
        [ConfigHelp<short>("Prize", "MinimumVirtual", ConfigScope.Arena, Description = "Distance from center of arena that prizes/flags/soccer-balls will spawn")]
        [ConfigHelp<short>("Prize", "UpgradeVirtual", ConfigScope.Arena, Description = "Amount of additional distance added to MinimumVirtual for each player that is in the game")]
        [ConfigHelp<short>("Prize", "PrizeMaxExist", ConfigScope.Arena, Description = "Maximum amount of time that a hidden prize will remain on screen. (actual time is random)")]
        [ConfigHelp<short>("Prize", "PrizeMinExist", ConfigScope.Arena, Description = "Minimum amount of time that a hidden prize will remain on screen. (actual time is random)")]
        [ConfigHelp<short>("Prize", "PrizeNegativeFactor", ConfigScope.Arena, Description = "Odds of getting a negative prize.  (1 = every prize, 32000 = extremely rare)")]
        [ConfigHelp<short>("Door", "DoorDelay", ConfigScope.Arena, Description = "How often doors attempt to switch their state")]
        [ConfigHelp<short>("Toggle", "AntiWarpPixels", ConfigScope.Arena, Description = "Distance Anti-Warp affects other players (in pixels) (note: enemy must also be on radar)")]
        [ConfigHelp<short>("Door", "DoorMode", ConfigScope.Arena, Description = "Door mode (-2=all doors completely random, -1=weighted random (some doors open more often than others), 0-255=fixed doors (1 bit of byte for each door specifying whether it is open or not)")]
        [ConfigHelp<short>("Flag", "FlagBlankDelay", ConfigScope.Arena, Description = "Amount of time that a user can get no data from server before flags are hidden from view for 10 seconds")]
        [ConfigHelp<short>("Flag", "NoDataFlagDropDelay", ConfigScope.Arena, Description = "Amount of time that a user can get no data from server before flags he is carrying are dropped")]
        [ConfigHelp<short>("Prize", "MultiPrizeCount", ConfigScope.Arena, Description = "Number of random greens given with a MultiPrize")]
        [ConfigHelp<short>("Brick", "BrickTime", ConfigScope.Arena, Description = "How long bricks last (in ticks)")]
        [ConfigHelp<short>("Misc", "WarpRadiusLimit", ConfigScope.Arena, Description = "When ships are randomly placed in the arena, this parameter will limit how far from the center of the arena they can be placed (1024=anywhere)")]
        [ConfigHelp<short>("Bomb", "EBombShutdownTime", ConfigScope.Arena, Description = "Maximum time recharge is stopped on players hit with an EMP bomb")]
        [ConfigHelp<short>("Bomb", "EBombDamagePercent", ConfigScope.Arena, Description = "Percentage of normal damage applied to an EMP bomb (in 0.1%)")]
        [ConfigHelp<short>("Radar", "RadarNeutralSize", ConfigScope.Arena, Description = "Size of area between blinded radar zones (in pixels)")]
        [ConfigHelp<short>("Misc", "WarpPointDelay", ConfigScope.Arena, Description = "How long a portal is active")]
        [ConfigHelp<short>("Misc", "NearDeathLevel", ConfigScope.Arena, Description = "Amount of energy that constitutes a near-death experience (ships bounty will be decreased by 1 when this occurs -- used for dueling zone)")]
        [ConfigHelp<short>("Bomb", "BBombDamagePercent", ConfigScope.Arena, Description = "Percentage of normal damage applied to a bouncing bomb (in 0.1%)")]
        [ConfigHelp<short>("Shrapnel", "ShrapnelDamagePercent", ConfigScope.Arena, Description = "Percentage of normal damage applied to shrapnel (relative to bullets of same level) (in 0.1%)")]
        [ConfigHelp<short>("Latency", "ClientSlowPacketTime", ConfigScope.Arena, Description = "Amount of latency S2C that constitutes a slow packet")]
        [ConfigHelp<short>("Flag", "FlagDropResetReward", ConfigScope.Arena, Description = "Minimum kill reward that a player must get in order to have his flag drop timer reset")]
        [ConfigHelp<short>("Flag", "FlaggerFireCostPercent", ConfigScope.Arena, Description = "Percentage of normal weapon firing cost for flaggers (in 0.1%)")]
        [ConfigHelp<short>("Flag", "FlaggerDamagePercent", ConfigScope.Arena, Description = "Percentage of normal damage received by flaggers (in 0.1%)")]
        [ConfigHelp<short>("Flag", "FlaggerBombFireDelay", ConfigScope.Arena, Description = "Delay given to flaggers for firing bombs (zero is ships normal firing rate) (do not set this number less than 20)")]
        [ConfigHelp<short>("Soccer", "PassDelay", ConfigScope.Arena, Description = "How long after the ball is fired before anybody can pick it up (in ticks)")]
        [ConfigHelp<short>("Soccer", "BallBlankDelay", ConfigScope.Arena, Description = "Amount of time a player can receive no data from server and still pick up the soccer ball")]
        [ConfigHelp<short>("Latency", "S2CNoDataKickoutDelay", ConfigScope.Arena, Description = "Amount of time a user can receive no data from server before connection is terminated")]
        [ConfigHelp<short>("Flag", "FlaggerThrustAdjustment", ConfigScope.Arena, Description = "Amount of thrust adjustment player carrying flag gets (negative numbers mean less thrust)")]
        [ConfigHelp<short>("Flag", "FlaggerSpeedAdjustment", ConfigScope.Arena, Description = "Amount of speed adjustment player carrying flag gets (negative numbers mean slower)")]
        [ConfigHelp<short>("Latency", "ClientSlowPacketSampleSize", ConfigScope.Arena, Description = "Number of packets to sample S2C before checking for kickout")]
        public static readonly (string Section, string Key)[] ShortNames =
        [
            ("Latency", "SendRoutePercent"),
            ("Bomb", "BombExplodeDelay"),
            ("Misc", "SendPositionDelay"),
            ("Bomb", "BombExplodePixels"),
            ("Prize", "DeathPrizeTime"),
            ("Bomb", "JitterTime"),
            ("Kill", "EnterDelay"),
            ("Prize", "EngineShutdownTime"),
            ("Bomb", "ProximityDistance"),
            ("Kill", "BountyIncreaseForKill"),
            ("Misc", "BounceFactor"),
            ("Radar", "MapZoomFactor"),
            ("Kill", "MaxBonus"),
            ("Kill", "MaxPenalty"),
            ("Kill", "RewardBase"),
            ("Repel", "RepelTime"),
            ("Repel", "RepelDistance"),
            ("Misc", "TickerDelay"),
            ("Flag", "FlaggerOnRadar"),
            ("Flag", "FlaggerKillMultiplier"),
            ("Prize", "PrizeFactor"),
            ("Prize", "PrizeDelay"),
            ("Prize", "MinimumVirtual"),
            ("Prize", "UpgradeVirtual"),
            ("Prize", "PrizeMaxExist"),
            ("Prize", "PrizeMinExist"),
            ("Prize", "PrizeNegativeFactor"),
            ("Door", "DoorDelay"),
            ("Toggle", "AntiWarpPixels"),
            ("Door", "DoorMode"),
            ("Flag", "FlagBlankDelay"),
            ("Flag", "NoDataFlagDropDelay"),
            ("Prize", "MultiPrizeCount"),
            ("Brick", "BrickTime"),
            ("Misc", "WarpRadiusLimit"),
            ("Bomb", "EBombShutdownTime"),
            ("Bomb", "EBombDamagePercent"),
            ("Radar", "RadarNeutralSize"),
            ("Misc", "WarpPointDelay"),
            ("Misc", "NearDeathLevel"),
            ("Bomb", "BBombDamagePercent"),
            ("Shrapnel", "ShrapnelDamagePercent"),
            ("Latency", "ClientSlowPacketTime"),
            ("Flag", "FlagDropResetReward"),
            ("Flag", "FlaggerFireCostPercent"),
            ("Flag", "FlaggerDamagePercent"),
            ("Flag", "FlaggerBombFireDelay"),
            ("Soccer", "PassDelay"),
            ("Soccer", "BallBlankDelay"),
            ("Latency", "S2CNoDataKickoutDelay"),
            ("Flag", "FlaggerThrustAdjustment"),
            ("Flag", "FlaggerSpeedAdjustment"),
            ("Latency", "ClientSlowPacketSampleSize"),
            ("Unused", "Unused5"),
            ("Unused", "Unused4"),
            ("Unused", "Unused3"),
            ("Unused", "Unused2"),
            ("Unused", "Unused1"),
        ];

        [ConfigHelp<bool>("Shrapnel", "Random", ConfigScope.Arena, Description = "Whether shrapnel spreads in circular or random patterns")]
        [ConfigHelp<bool>("Soccer", "BallBounce", ConfigScope.Arena, Description = "Whether the ball bounces off walls")]
        [ConfigHelp<bool>("Soccer", "AllowBombs", ConfigScope.Arena, Description = "Whether the ball carrier can fire his bombs")]
        [ConfigHelp<bool>("Soccer", "AllowGuns", ConfigScope.Arena, Description = "Whether the ball carrier can fire his guns")]
        [ConfigHelp<SoccerMode>("Soccer", "Mode", ConfigScope.Arena,
            Description = """
                Goal configuration:
                All = All goals are open for scoring by any freq,
                LeftRight = Left vs Right: Even freqs (defend left side) vs odd freqs (defend right side),
                TopBottom = Top vs Bottom: Even freqs (defend top) vs odd freqs (defend bottom),
                QuadrantsDefend1 = 4 quadrants, 1 quadrant to defend,
                QuadrantsDefend3 = 4 quadrants, 3 quadrants to defend,
                SidesDefend1 = 4 sides, 1 side to defend,
                SidesDefend3 = 4 sides, 3 sides to defend
                """)]
        //Team:MaxPerTeam
        //Team:MaxPerPrivateTeam
        [ConfigHelp<byte>("Mine", "TeamMaxMines", ConfigScope.Arena, Description = "Maximum number of mines allowed to be placed by an entire team")]
        [ConfigHelp<bool>("Wormhole", "GravityBombs", ConfigScope.Arena, Description = "Whether a wormhole affects bombs")]
        [ConfigHelp<bool>("Bomb", "BombSafety", ConfigScope.Arena, Description = "Whether proximity bombs have a firing safety.  If enemy ship is within proximity radius, will it allow you to fire")]
        //Chat:MessageReliable
        [ConfigHelp<byte>("Prize", "TakePrizeReliable", ConfigScope.Arena, Description = "Whether prize packets are sent reliably (C2S)")]
        [ConfigHelp<bool>("Message", "AllowAudioMessages", ConfigScope.Arena, Description = "Whether players can send audio messages")]
        [ConfigHelp<byte>("Prize", "PrizeHideCount", ConfigScope.Arena, Description = "Number of prizes that are regenerated every PrizeDelay")]
        [ConfigHelp<byte>("Misc", "ExtraPositionData", ConfigScope.Arena, Description = "Whether regular players receive sysop data about a ship")]
        [ConfigHelp<byte>("Misc", "SlowFrameCheck", ConfigScope.Arena, Description = "Whether to check for slow frames on the client (possible cheat technique) (flawed on some machines, do not use)")]
        [ConfigHelp<byte>("Flag", "CarryFlags", ConfigScope.Arena, Description = "Whether the flags can be picked up and carried (0=no, 1=yes, 2=yes-one at a time, 3=yes-two at a time, 4=three, etc..)")]
        [ConfigHelp<byte>("Misc", "AllowSavedShips", ConfigScope.Arena, Description = "Whether saved ships are allowed (do not allow saved ship in zones where sub-arenas may have differing parameters)")]
        [ConfigHelp<byte>("Radar", "RadarMode", ConfigScope.Arena, Description = "Radar mode (0=normal, 1=half/half, 2=quarters, 3=half/half-see team mates, 4=quarters-see team mates)")]
        [ConfigHelp<byte>("Misc", "VictoryMusic", ConfigScope.Arena, Description = "Whether the zone plays victory music or not")]
        [ConfigHelp<bool>("Flag", "FlaggerGunUpgrade", ConfigScope.Arena, Description = "Whether the flaggers get a gun upgrade")]
        [ConfigHelp<bool>("Flag", "FlaggerBombUpgrade", ConfigScope.Arena, Description = "Whether the flaggers get a bomb upgrade")]
        [ConfigHelp<bool>("Soccer", "UseFlagger", ConfigScope.Arena, Description = "If player with soccer ball should use the Flag:Flagger* ship adjustments or not")]
        [ConfigHelp<bool>("Soccer", "BallLocation", ConfigScope.Arena, Description = "Whether the balls location is displayed at all times or not")]
        [ConfigHelp<byte>("Misc", "AntiWarpSettleDelay", ConfigScope.Arena, Description = "How many ticks to activate a fake antiwarp after attaching, portaling, or warping.")]
        public static readonly (string Section, string Key)[] ByteNames =
        [
            ("Shrapnel", "Random"),
            ("Soccer", "BallBounce"),
            ("Soccer", "AllowBombs"),
            ("Soccer", "AllowGuns"),
            ("Soccer", "Mode"),
            ("Team", "MaxPerTeam"),
            ("Team", "MaxPerPrivateTeam"),
            ("Mine", "TeamMaxMines"),
            ("Wormhole", "GravityBombs"),
            ("Bomb", "BombSafety"),
            ("Chat", "MessageReliable"),
            ("Prize", "TakePrizeReliable"),
            ("Message", "AllowAudioMessages"),
            ("Prize", "PrizeHideCount"),
            ("Misc", "ExtraPositionData"),
            ("Misc", "SlowFrameCheck"),
            ("Flag", "CarryFlags"),
            ("Misc", "AllowSavedShips"),
            ("Radar", "RadarMode"),
            ("Misc", "VictoryMusic"),
            ("Flag", "FlaggerGunUpgrade"),
            ("Flag", "FlaggerBombUpgrade"),
            ("Soccer", "UseFlagger"),
            ("Soccer", "BallLocation"),
            ("Misc", "AntiWarpSettleDelay"),
            ("Unused", "Unused7"),
            ("Unused", "Unused6"),
            ("Unused", "Unused5"),
            ("Unused", "Unused4"),
            ("Unused", "Unused3"),
            ("Unused", "Unused2"),
            ("Unused", "Unused1"),
        ];

        [ConfigHelp<byte>("PrizeWeight", "QuickCharge", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Recharge' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Energy", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Energy Upgrade' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Rotation", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Rotation' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Stealth", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Stealth' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Cloak", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Cloak' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "XRadar", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'XRadar' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Warp", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Warp' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Gun", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Gun Upgrade' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Bomb", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Bomb Upgrade' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "BouncingBullets", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Bouncing Bullets' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Thruster", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Thruster' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "TopSpeed", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Speed' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Recharge", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Full Charge' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Glue", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Engine Shutdown' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "MultiFire", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'MultiFire' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Proximity", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Proximity Bomb' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "AllWeapons", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Super!' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Shields", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Shields' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Shrapnel", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Shrapnel Upgrade' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "AntiWarp", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'AntiWarp' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Repel", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Repel' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Burst", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Burst' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Decoy", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Decoy' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Thor", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Thor' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "MultiPrize", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Multi-Prize' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Brick", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Brick' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Rocket", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Rocket' prize appearing")]
        [ConfigHelp<byte>("PrizeWeight", "Portal", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Portal' prize appearing")]
        public static readonly (string Section, string Key)[] PrizeWeightNames =
        [
            ("PrizeWeight", "QuickCharge"),
            ("PrizeWeight", "Energy"),
            ("PrizeWeight", "Rotation"),
            ("PrizeWeight", "Stealth"),
            ("PrizeWeight", "Cloak"),
            ("PrizeWeight", "XRadar"),
            ("PrizeWeight", "Warp"),
            ("PrizeWeight", "Gun"),
            ("PrizeWeight", "Bomb"),
            ("PrizeWeight", "BouncingBullets"),
            ("PrizeWeight", "Thruster"),
            ("PrizeWeight", "TopSpeed"),
            ("PrizeWeight", "Recharge"),
            ("PrizeWeight", "Glue"),
            ("PrizeWeight", "MultiFire"),
            ("PrizeWeight", "Proximity"),
            ("PrizeWeight", "AllWeapons"),
            ("PrizeWeight", "Shields"),
            ("PrizeWeight", "Shrapnel"),
            ("PrizeWeight", "AntiWarp"),
            ("PrizeWeight", "Repel"),
            ("PrizeWeight", "Burst"),
            ("PrizeWeight", "Decoy"),
            ("PrizeWeight", "Thor"),
            ("PrizeWeight", "MultiPrize"),
            ("PrizeWeight", "Brick"),
            ("PrizeWeight", "Rocket"),
            ("PrizeWeight", "Portal"),
        ];

        [ConfigHelp<byte>("DPrizeWeight", "QuickCharge", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Recharge' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Energy", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Energy Upgrade' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Rotation", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Rotation' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Stealth", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Stealth' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Cloak", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Cloak' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "XRadar", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'XRadar' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Warp", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Warp' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Gun", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Gun Upgrade' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Bomb", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Bomb Upgrade' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "BouncingBullets", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Bouncing Bullets' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Thruster", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Thruster' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "TopSpeed", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Speed' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Recharge", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Full Charge' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Glue", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Engine Shutdown' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "MultiFire", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'MultiFire' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Proximity", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Proximity Bomb' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "AllWeapons", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Super!' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Shields", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Shields' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Shrapnel", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Shrapnel Upgrade' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "AntiWarp", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'AntiWarp' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Repel", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Repel' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Burst", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Burst' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Decoy", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Decoy' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Thor", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Thor' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "MultiPrize", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Multi-Prize' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Brick", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Brick' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Rocket", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Rocket' prize appearing")]
        [ConfigHelp<byte>("DPrizeWeight", "Portal", ConfigScope.Arena, Default = 0, Min = 0, Max = 255, Description = "Likelihood of 'Portal' prize appearing")]
        public static readonly (string Section, string Key)[] DeathPrizeWeightNames =
        [
            ("DPrizeWeight", "QuickCharge"),
            ("DPrizeWeight", "Energy"),
            ("DPrizeWeight", "Rotation"),
            ("DPrizeWeight", "Stealth"),
            ("DPrizeWeight", "Cloak"),
            ("DPrizeWeight", "XRadar"),
            ("DPrizeWeight", "Warp"),
            ("DPrizeWeight", "Gun"),
            ("DPrizeWeight", "Bomb"),
            ("DPrizeWeight", "BouncingBullets"),
            ("DPrizeWeight", "Thruster"),
            ("DPrizeWeight", "TopSpeed"),
            ("DPrizeWeight", "Recharge"),
            ("DPrizeWeight", "Glue"),
            ("DPrizeWeight", "MultiFire"),
            ("DPrizeWeight", "Proximity"),
            ("DPrizeWeight", "AllWeapons"),
            ("DPrizeWeight", "Shields"),
            ("DPrizeWeight", "Shrapnel"),
            ("DPrizeWeight", "AntiWarp"),
            ("DPrizeWeight", "Repel"),
            ("DPrizeWeight", "Burst"),
            ("DPrizeWeight", "Decoy"),
            ("DPrizeWeight", "Thor"),
            ("DPrizeWeight", "MultiPrize"),
            ("DPrizeWeight", "Brick"),
            ("DPrizeWeight", "Rocket"),
            ("DPrizeWeight", "Portal"),
        ];

        /* the following names are only key names, not key+section names */

        [ConfigHelp<int>("All", "SuperTime", ConfigScope.Arena, Description = "How long Super lasts on the ship (in ticks)")]
        [ConfigHelp<int>("All", "ShieldsTime", ConfigScope.Arena, Description = "How long Shields lasts on the ship (in ticks)")]
        public static readonly string[] ShipLongNames =
        [
            "SuperTime",
            "ShieldsTime"
        ];

        [ConfigHelp<short>("All", "Gravity", ConfigScope.Arena, Description = "How strong of an effect the wormhole has on this ship (0 = none)")]
        [ConfigHelp<short>("All", "GravityTopSpeed", ConfigScope.Arena, Description = "Ship are allowed to move faster than their maximum speed while effected by a wormhole.  This determines how much faster they can go (0 = no extra speed)")]
        [ConfigHelp<short>("All", "BulletFireEnergy", ConfigScope.Arena, Description = "Amount of energy it takes a ship to fire a single L1 bullet")]
        [ConfigHelp<short>("All", "MultiFireEnergy", ConfigScope.Arena, Description = "Amount of energy it takes a ship to fire multifire L1 bullets")]
        [ConfigHelp<short>("All", "BombFireEnergy", ConfigScope.Arena, Description = "Amount of energy it takes a ship to fire a single bomb")]
        [ConfigHelp<short>("All", "BombFireEnergyUpgrade", ConfigScope.Arena, Description = "Extra amount of energy it takes a ship to fire an upgraded bomb. i.e. L2 = BombFireEnergy+BombFireEnergyUpgrade")]
        [ConfigHelp<short>("All", "LandmineFireEnergy", ConfigScope.Arena, Description = "Amount of energy it takes a ship to place a single L1 mine")]
        [ConfigHelp<short>("All", "LandmineFireEnergyUpgrade", ConfigScope.Arena, Description = "Extra amount of energy it takes to place an upgraded landmine. i.e. L2 = LandmineFireEnergy+LandmineFireEnergyUpgrade")]
        [ConfigHelp<short>("All", "BulletSpeed", ConfigScope.Arena, Description = "How fast bullets travel")]
        [ConfigHelp<short>("All", "BombSpeed", ConfigScope.Arena, Description = "How fast bombs travel")]
        [ConfigHelp<short>("All", "MultiFireAngle", ConfigScope.Arena, Description = "Angle spread between multi-fire bullets and standard forward firing bullets (111 = 1 degree, 1000 = 1 ship-rotation-point)")]
        [ConfigHelp<short>("All", "CloakEnergy", ConfigScope.Arena, Description = "Amount of energy required to have 'Cloak' activated (thousanths per tick)")]
        [ConfigHelp<short>("All", "StealthEnergy", ConfigScope.Arena, Description = "Amount of energy required to have 'Stealth' activated (thousanths per tick)")]
        [ConfigHelp<short>("All", "AntiWarpEnergy", ConfigScope.Arena, Description = "Amount of energy required to have 'Anti-Warp' activated (thousanths per tick)")]
        [ConfigHelp<short>("All", "XRadarEnergy", ConfigScope.Arena, Description = "Amount of energy required to have 'X-Radar' activated (thousanths per tick)")]
        [ConfigHelp<short>("All", "MaximumRotation", ConfigScope.Arena, Description = "Maximum rotation rate of the ship (0 = can't rotate, 400 = full rotation in 1 second)")]
        [ConfigHelp<short>("All", "MaximumThrust", ConfigScope.Arena, Description = "Maximum thrust of ship (0 = none)")]
        [ConfigHelp<short>("All", "MaximumSpeed", ConfigScope.Arena, Description = "Maximum speed of ship (0 = can't move)")]
        [ConfigHelp<short>("All", "MaximumRecharge", ConfigScope.Arena, Description = "Maximum recharge rate, or how quickly this ship recharges its energy")]
        [ConfigHelp<short>("All", "MaximumEnergy", ConfigScope.Arena, Description = "Maximum amount of energy that the ship can have")]
        [ConfigHelp<short>("All", "InitialRotation", ConfigScope.Arena, Description = "Initial rotation rate of the ship (0 = can't rotate, 400 = full rotation in 1 second)")]
        [ConfigHelp<short>("All", "InitialThrust", ConfigScope.Arena, Description = "Initial thrust of ship (0 = none)")]
        [ConfigHelp<short>("All", "InitialSpeed", ConfigScope.Arena, Description = "Initial speed of ship (0 = can't move)")]
        [ConfigHelp<short>("All", "InitialRecharge", ConfigScope.Arena, Description = "Initial recharge rate, or how quickly this ship recharges its energy")]
        [ConfigHelp<short>("All", "InitialEnergy", ConfigScope.Arena, Description = "Initial amount of energy that the ship can have")]
        [ConfigHelp<short>("All", "UpgradeRotation", ConfigScope.Arena, Description = "Amount added per 'Rotation' Prize")]
        [ConfigHelp<short>("All", "UpgradeThrust", ConfigScope.Arena, Description = "Amount added per 'Thruster' Prize")]
        [ConfigHelp<short>("All", "UpgradeSpeed", ConfigScope.Arena, Description = "Amount added per 'Speed' Prize")]
        [ConfigHelp<short>("All", "UpgradeRecharge", ConfigScope.Arena, Description = "Amount added per 'Recharge Rate' Prize")]
        [ConfigHelp<short>("All", "UpgradeEnergy", ConfigScope.Arena, Description = "Amount added per 'Energy Upgrade' Prize")]
        [ConfigHelp<short>("All", "AfterburnerEnergy", ConfigScope.Arena, Description = "Amount of energy required to have 'Afterburners' activated")]
        [ConfigHelp<short>("All", "BombThrust", ConfigScope.Arena, Description = "Amount of back-thrust you receive when firing a bomb")]
        [ConfigHelp<short>("All", "BurstSpeed", ConfigScope.Arena, Description = "How fast the burst shrapnel is for this ship")]
        [ConfigHelp<short>("All", "TurretThrustPenalty", ConfigScope.Arena, Description = "Amount the ship's thrust is decreased with a turret riding")]
        [ConfigHelp<short>("All", "TurretSpeedPenalty", ConfigScope.Arena, Description = "Amount the ship's speed is decreased with a turret riding")]
        [ConfigHelp<short>("All", "BulletFireDelay", ConfigScope.Arena, Description = "Delay that ship waits after a bullet is fired until another weapon may be fired (in ticks)")]
        [ConfigHelp<short>("All", "MultiFireDelay", ConfigScope.Arena, Description = "Delay that ship waits after a multifire bullet is fired until another weapon may be fired (in ticks)")]
        [ConfigHelp<short>("All", "BombFireDelay", ConfigScope.Arena, Description = "Delay that ship waits after a bomb is fired until another weapon may be fired (in ticks)")]
        [ConfigHelp<short>("All", "LandmineFireDelay", ConfigScope.Arena, Description = "Delay that ship waits after a mine is fired until another weapon may be fired (in ticks)")]
        [ConfigHelp<short>("All", "RocketTime", ConfigScope.Arena, Description = "How long a Rocket lasts (in ticks)")]
        [ConfigHelp<short>("All", "InitialBounty", ConfigScope.Arena, Description = "Number of 'Greens' given to ships when they start")]
        [ConfigHelp<short>("All", "DamageFactor", ConfigScope.Arena, Description = "How likely a the ship is to take damamage (ie. lose a prize) (0=special-case-never, 1=extremely likely, 5000=almost never)")]
        [ConfigHelp<short>("All", "PrizeShareLimit", ConfigScope.Arena, Description = "Maximum bounty that ships receive Team Prizes")]
        [ConfigHelp<short>("All", "AttachBounty", ConfigScope.Arena, Description = "Bounty required by ships to attach as a turret")]
        [ConfigHelp<short>("All", "SoccerThrowTime", ConfigScope.Arena, Description = "Time player has to carry soccer ball (in ticks)")]
        [ConfigHelp<short>("All", "SoccerBallFriction", ConfigScope.Arena, Description = "Amount the friction on the soccer ball (how quickly it slows down -- higher numbers mean faster slowdown)")]
        [ConfigHelp<short>("All", "SoccerBallProximity", ConfigScope.Arena, Description = "How close the player must be in order to pick up ball (in pixels)")]
        [ConfigHelp<short>("All", "SoccerBallSpeed", ConfigScope.Arena, Description = "Initial speed given to the ball when fired by the carrier")]
        public static readonly string[] ShipShortNames =
        [
            "Gravity",
            "GravityTopSpeed",
            "BulletFireEnergy",
            "MultiFireEnergy",
            "BombFireEnergy",
            "BombFireEnergyUpgrade",
            "LandmineFireEnergy",
            "LandmineFireEnergyUpgrade",
            "BulletSpeed",
            "BombSpeed",
            "___MiscBitfield___",
            "MultiFireAngle",
            "CloakEnergy",
            "StealthEnergy",
            "AntiWarpEnergy",
            "XRadarEnergy",
            "MaximumRotation",
            "MaximumThrust",
            "MaximumSpeed",
            "MaximumRecharge",
            "MaximumEnergy",
            "InitialRotation",
            "InitialThrust",
            "InitialSpeed",
            "InitialRecharge",
            "InitialEnergy",
            "UpgradeRotation",
            "UpgradeThrust",
            "UpgradeSpeed",
            "UpgradeRecharge",
            "UpgradeEnergy",
            "AfterburnerEnergy",
            "BombThrust",
            "BurstSpeed",
            "TurretThrustPenalty",
            "TurretSpeedPenalty",
            "BulletFireDelay",
            "MultiFireDelay",
            "BombFireDelay",
            "LandmineFireDelay",
            "RocketTime",
            "InitialBounty",
            "DamageFactor",
            "PrizeShareLimit",
            "AttachBounty",
            "SoccerThrowTime",
            "SoccerBallFriction",
            "SoccerBallProximity",
            "SoccerBallSpeed"
        ];

        [ConfigHelp<byte>("All", "TurretLimit", ConfigScope.Arena, Description = "Number of turrets allowed on a ship")]
        [ConfigHelp<byte>("All", "BurstShrapnel", ConfigScope.Arena, Description = "Number of bullets released when a 'Burst' is activated")]
        [ConfigHelp<byte>("All", "MaxMines", ConfigScope.Arena, Description = "Maximum number of mines allowed in ships")]
        [ConfigHelp<byte>("All", "RepelMax", ConfigScope.Arena, Description = "Maximum number of Repels allowed in ships")]
        [ConfigHelp<byte>("All", "BurstMax", ConfigScope.Arena, Description = "Maximum number of Bursts allowed in ships")]
        [ConfigHelp<byte>("All", "DecoyMax", ConfigScope.Arena, Description = "Maximum number of Decoys allowed in ships")]
        [ConfigHelp<byte>("All", "ThorMax", ConfigScope.Arena, Description = "Maximum number of Thor's Hammers allowed in ships")]
        [ConfigHelp<byte>("All", "BrickMax", ConfigScope.Arena, Description = "Maximum number of Bricks allowed in ships")]
        [ConfigHelp<byte>("All", "RocketMax", ConfigScope.Arena, Description = "Maximum number of Rockets allowed in ships")]
        [ConfigHelp<byte>("All", "PortalMax", ConfigScope.Arena, Description = "Maximum number of Portals allowed in ships")]
        [ConfigHelp<byte>("All", "InitialRepel", ConfigScope.Arena, Description = "Initial number of Repels given to ships when they start")]
        [ConfigHelp<byte>("All", "InitialBurst", ConfigScope.Arena, Description = "Initial number of Bursts given to ships when they start")]
        [ConfigHelp<byte>("All", "InitialBrick", ConfigScope.Arena, Description = "Initial number of Bricks given to ships when they start")]
        [ConfigHelp<byte>("All", "InitialRocket", ConfigScope.Arena, Description = "Initial number of Rockets given to ships when they start")]
        [ConfigHelp<byte>("All", "InitialThor", ConfigScope.Arena, Description = "Initial number of Thor's Hammers given to ships when they start")]
        [ConfigHelp<byte>("All", "InitialDecoy", ConfigScope.Arena, Description = "Initial number of Decoys given to ships when they start")]
        [ConfigHelp<byte>("All", "InitialPortal", ConfigScope.Arena, Description = "Initial number of Portals given to ships when they start")]
        [ConfigHelp<byte>("All", "BombBounceCount", ConfigScope.Arena, Description = "Number of times a ship's bombs bounce before they explode on impact")]
        public static readonly string[] ShipByteNames =
        [
            "TurretLimit",
            "BurstShrapnel",
            "MaxMines",
            "RepelMax",
            "BurstMax",
            "DecoyMax",
            "ThorMax",
            "BrickMax",
            "RocketMax",
            "PortalMax",
            "InitialRepel",
            "InitialBurst",
            "InitialBrick",
            "InitialRocket",
            "InitialThor",
            "InitialDecoy",
            "InitialPortal",
            "BombBounceCount"
        ];
    }
}

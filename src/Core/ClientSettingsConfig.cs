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
        [ConfigHelp("Bullet", "BulletDamageLevel", ConfigScope.Arena, typeof(int), Description = "Maximum amount of damage that a L1 bullet will cause")]
        [ConfigHelp("Bomb", "BombDamageLevel", ConfigScope.Arena, typeof(int), Description = "Amount of damage a bomb causes at its center point (for all bomb levels)")]
        [ConfigHelp("Bullet", "BulletAliveTime", ConfigScope.Arena, typeof(int), Description = "How long bullets live before disappearing (in ticks)")]
        [ConfigHelp("Bomb", "BombAliveTime", ConfigScope.Arena, typeof(int), Description = "Time bomb is alive (in ticks)")]
        [ConfigHelp("Misc", "DecoyAliveTime", ConfigScope.Arena, typeof(int), "Time a decoy is alive (in ticks)")]
        [ConfigHelp("Misc", "SafetyLimit", ConfigScope.Arena, typeof(int), "Amount of time that can be spent in the safe zone (in ticks)")]
        [ConfigHelp("Misc", "FrequencyShift", ConfigScope.Arena, typeof(int), "Amount of random frequency shift applied to sounds in the game")]
        [ConfigHelp("Team", "MaxFrequency", ConfigScope.Arena, typeof(int), "Maximum number of frequencies allowed in arena (5 would allow frequencies 0,1,2,3,4)")]
        [ConfigHelp("Repel", "RepelSpeed", ConfigScope.Arena, typeof(int), "Speed at which players are repelled")]
        [ConfigHelp("Mine", "MineAliveTime", ConfigScope.Arena, typeof(int), "Time that mines are active (in ticks)")]
        [ConfigHelp("Burst", "BurstDamageLevel", ConfigScope.Arena, typeof(int), "Maximum amount of damage caused by a single burst bullet")]
        [ConfigHelp("Bullet", "BulletDamageUpgrade", ConfigScope.Arena, typeof(int), "Amount of extra damage each bullet level will cause")]
        [ConfigHelp("Flag", "FlagDropDelay", ConfigScope.Arena, typeof(int), "Time before flag is dropped by carrier (0=never)")]
        [ConfigHelp("Flag", "EnterGameFlaggingDelay", ConfigScope.Arena, typeof(int), "Time a new player must wait before they are allowed to see flags")]
        [ConfigHelp("Rocket", "RocketThrust", ConfigScope.Arena, typeof(int), "Thrust value given while a rocket is active")]
        [ConfigHelp("Rocket", "RocketSpeed", ConfigScope.Arena, typeof(int), "Speed value given while a rocket is active")]
        [ConfigHelp("Shrapnel", "InactiveShrapDamage", ConfigScope.Arena, typeof(int), "Amount of damage shrapnel causes in it's first 1/4 second of life")]
        [ConfigHelp("Wormhole", "SwitchTime", ConfigScope.Arena, typeof(int), "How often the wormhole switches its destination")]
        [ConfigHelp("Misc", "ActivateAppShutdownTime", ConfigScope.Arena, typeof(int), "Amount of time a ship is shutdown after application is reactivated")]
        [ConfigHelp("Shrapnel", "ShrapnelSpeed", ConfigScope.Arena, typeof(int), "Speed that shrapnel travels")]
        public static readonly (string Section, string Key)[] LongNames =
        {
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
        };

        [ConfigHelp("Latency", "SendRoutePercent", ConfigScope.Arena, typeof(short), "Percentage of the ping time that is spent on the C2S portion of the ping (used in more accurately syncronizing clocks)")]
        [ConfigHelp("Bomb", "BombExplodeDelay", ConfigScope.Arena, typeof(short), "How long after the proximity sensor is triggered before bomb explodes")]
        [ConfigHelp("Misc", "SendPositionDelay", ConfigScope.Arena, typeof(short), "Amount of time between position packets sent by client")]
        [ConfigHelp("Bomb", "BombExplodePixels", ConfigScope.Arena, typeof(short), "Blast radius in pixels for an L1 bomb (L2 bombs double this, L3 bombs triple this)")]
        [ConfigHelp("Prize", "DeathPrizeTime", ConfigScope.Arena, typeof(short), "How long the prize exists that appears after killing somebody")]
        [ConfigHelp("Bomb", "JitterTime", ConfigScope.Arena, typeof(short), "How long the screen jitters from a bomb hit (in ticks)")]
        [ConfigHelp("Kill", "EnterDelay", ConfigScope.Arena, typeof(short), "How long after a player dies before he can re-enter the game (in ticks)")]
        [ConfigHelp("Prize", "EngineShutdownTime", ConfigScope.Arena, typeof(short), "Time the player is affected by an 'Engine Shutdown' Prize (in ticks)")]
        [ConfigHelp("Bomb", "ProximityDistance", ConfigScope.Arena, typeof(short), "Radius of proximity trigger in tiles (each bomb level adds 1 to this amount)")]
        [ConfigHelp("Kill", "BountyIncreaseForKill", ConfigScope.Arena, typeof(short), "Number of points added to players bounty each time he kills an opponent")]
        [ConfigHelp("Misc", "BounceFactor", ConfigScope.Arena, typeof(short), "How bouncy the walls are (16 = no speed loss)")]
        [ConfigHelp("Radar", "MapZoomFactor", ConfigScope.Arena, typeof(short), "A number representing how much the map is zoomed out for radar. (48 = whole map on radar, 49+ = effectively disable radar)")]
        [ConfigHelp("Kill", "MaxBonus", ConfigScope.Arena, typeof(short), "This is if you have flags, can add more points per a kill.")]
        [ConfigHelp("Kill", "MaxPenalty", ConfigScope.Arena, typeof(short), "This is if you have flags, can take away points per a kill.")]
        [ConfigHelp("Kill", "RewardBase", ConfigScope.Arena, typeof(short), "This is shown added to a person's bty, but isn't added from points for a kill.")]
        [ConfigHelp("Repel", "RepelTime", ConfigScope.Arena, typeof(short), "Time players are affected by the repel (in ticks)")]
        [ConfigHelp("Repel", "RepelDistance", ConfigScope.Arena, typeof(short), "Number of pixels from the player that are affected by a repel")]
        [ConfigHelp("Misc", "TickerDelay", ConfigScope.Arena, typeof(short), "Amount of time between ticker help messages")]
        [ConfigHelp("Flag", "FlaggerOnRadar", ConfigScope.Arena, typeof(bool), "Whether the flaggers appear on radar in red")]
        [ConfigHelp("Flag", "FlaggerKillMultiplier", ConfigScope.Arena, typeof(short), "Number of times more points are given to a flagger (1 = double points, 2 = triple points)")]
        [ConfigHelp("Prize", "PrizeFactor", ConfigScope.Arena, typeof(short), "Number of prizes hidden is based on number of players in game. This number adjusts the formula, higher numbers mean more prizes. (Note: 10000 is max, 10 greens per person)")]
        [ConfigHelp("Prize", "PrizeDelay", ConfigScope.Arena, typeof(short), "How often prizes are regenerated (in ticks)")]
        [ConfigHelp("Prize", "MinimumVirtual", ConfigScope.Arena, typeof(short), "Distance from center of arena that prizes/flags/soccer-balls will spawn")]
        [ConfigHelp("Prize", "UpgradeVirtual", ConfigScope.Arena, typeof(short), "Amount of additional distance added to MinimumVirtual for each player that is in the game")]
        [ConfigHelp("Prize", "PrizeMaxExist", ConfigScope.Arena, typeof(short), "Maximum amount of time that a hidden prize will remain on screen. (actual time is random)")]
        [ConfigHelp("Prize", "PrizeMinExist", ConfigScope.Arena, typeof(short), "Minimum amount of time that a hidden prize will remain on screen. (actual time is random)")]
        [ConfigHelp("Prize", "PrizeNegativeFactor", ConfigScope.Arena, typeof(short), "Odds of getting a negative prize.  (1 = every prize, 32000 = extremely rare)")]
        [ConfigHelp("Door", "DoorDelay", ConfigScope.Arena, typeof(short), "How often doors attempt to switch their state")]
        [ConfigHelp("Toggle", "AntiWarpPixels", ConfigScope.Arena, typeof(short), "Distance Anti-Warp affects other players (in pixels) (note: enemy must also be on radar)")]
        [ConfigHelp("Door", "DoorMode", ConfigScope.Arena, typeof(short), "Door mode (-2=all doors completely random, -1=weighted random (some doors open more often than others), 0-255=fixed doors (1 bit of byte for each door specifying whether it is open or not)")]
        [ConfigHelp("Flag", "FlagBlankDelay", ConfigScope.Arena, typeof(short), "Amount of time that a user can get no data from server before flags are hidden from view for 10 seconds")]
        [ConfigHelp("Flag", "NoDataFlagDropDelay", ConfigScope.Arena, typeof(short), "Amount of time that a user can get no data from server before flags he is carrying are dropped")]
        [ConfigHelp("Prize", "MultiPrizeCount", ConfigScope.Arena, typeof(short), "Number of random greens given with a MultiPrize")]
        [ConfigHelp("Brick", "BrickTime", ConfigScope.Arena, typeof(short), "How long bricks last (in ticks)")]
        [ConfigHelp("Misc", "WarpRadiusLimit", ConfigScope.Arena, typeof(short), "When ships are randomly placed in the arena, this parameter will limit how far from the center of the arena they can be placed (1024=anywhere)")]
        [ConfigHelp("Bomb", "EBombShutdownTime", ConfigScope.Arena, typeof(short), "Maximum time recharge is stopped on players hit with an EMP bomb")]
        [ConfigHelp("Bomb", "EBombDamagePercent", ConfigScope.Arena, typeof(short), "Percentage of normal damage applied to an EMP bomb (in 0.1%)")]
        [ConfigHelp("Radar", "RadarNeutralSize", ConfigScope.Arena, typeof(short), "Size of area between blinded radar zones (in pixels)")]
        [ConfigHelp("Misc", "WarpPointDelay", ConfigScope.Arena, typeof(short), "How long a portal is active")]
        [ConfigHelp("Misc", "NearDeathLevel", ConfigScope.Arena, typeof(short), "Amount of energy that constitutes a near-death experience (ships bounty will be decreased by 1 when this occurs -- used for dueling zone)")]
        [ConfigHelp("Bomb", "BBombDamagePercent", ConfigScope.Arena, typeof(short), "Percentage of normal damage applied to a bouncing bomb (in 0.1%)")]
        [ConfigHelp("Shrapnel", "ShrapnelDamagePercent", ConfigScope.Arena, typeof(short), "Percentage of normal damage applied to shrapnel (relative to bullets of same level) (in 0.1%)")]
        [ConfigHelp("Latency", "ClientSlowPacketTime", ConfigScope.Arena, typeof(short), "Amount of latency S2C that constitutes a slow packet")]
        [ConfigHelp("Flag", "FlagDropResetReward", ConfigScope.Arena, typeof(short), "Minimum kill reward that a player must get in order to have his flag drop timer reset")]
        [ConfigHelp("Flag", "FlaggerFireCostPercent", ConfigScope.Arena, typeof(short), "Percentage of normal weapon firing cost for flaggers (in 0.1%)")]
        [ConfigHelp("Flag", "FlaggerDamagePercent", ConfigScope.Arena, typeof(short), "Percentage of normal damage received by flaggers (in 0.1%)")]
        [ConfigHelp("Flag", "FlaggerBombFireDelay", ConfigScope.Arena, typeof(short), "Delay given to flaggers for firing bombs (zero is ships normal firing rate) (do not set this number less than 20)")]
        [ConfigHelp("Soccer", "PassDelay", ConfigScope.Arena, typeof(short), "How long after the ball is fired before anybody can pick it up (in ticks)")]
        [ConfigHelp("Soccer", "BallBlankDelay", ConfigScope.Arena, typeof(short), "Amount of time a player can receive no data from server and still pick up the soccer ball")]
        [ConfigHelp("Latency", "S2CNoDataKickoutDelay", ConfigScope.Arena, typeof(short), "Amount of time a user can receive no data from server before connection is terminated")]
        [ConfigHelp("Flag", "FlaggerThrustAdjustment", ConfigScope.Arena, typeof(short), "Amount of thrust adjustment player carrying flag gets (negative numbers mean less thrust)")]
        [ConfigHelp("Flag", "FlaggerSpeedAdjustment", ConfigScope.Arena, typeof(short), "Amount of speed adjustment player carrying flag gets (negative numbers mean slower)")]
        [ConfigHelp("Latency", "ClientSlowPacketSampleSize", ConfigScope.Arena, typeof(short), "Number of packets to sample S2C before checking for kickout")]
        public static readonly (string Section, string Key)[] ShortNames =
        {
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
        };

        [ConfigHelp("Shrapnel", "Random", ConfigScope.Arena, typeof(bool), "Whether shrapnel spreads in circular or random patterns")]
        [ConfigHelp("Soccer", "BallBounce", ConfigScope.Arena, typeof(bool), "Whether the ball bounces off walls")]
        [ConfigHelp("Soccer", "AllowBombs", ConfigScope.Arena, typeof(bool), "Whether the ball carrier can fire his bombs")]
        [ConfigHelp("Soccer", "AllowGuns", ConfigScope.Arena, typeof(bool), "Whether the ball carrier can fire his guns")]
        [ConfigHelp("Soccer", "Mode", ConfigScope.Arena, typeof(SoccerMode),
            Description = "Goal configuration: " +
            "All = All goals are open for scoring by any freq, " +
            "LeftRight = Left vs Right: Even freqs (defend left side) vs odd freqs (defend right side), " +
            "TopBottom = Top vs Bottom: Even freqs (defend top) vs odd freqs (defend bottom), " +
            "QuadrantsDefend1 = 4 quadrants, 1 quadrant to defend, " +
            "QuadrantsDefend3 = 4 quadrants, 3 quadrants to defend, " +
            "SidesDefend1 = 4 sides, 1 side to defend, " +
            "SidesDefend3 = 4 sides, 3 sides to defend")]
        //Team:MaxPerTeam
        //Team:MaxPerPrivateTeam
        [ConfigHelp("Mine", "TeamMaxMines", ConfigScope.Arena, typeof(byte), "Maximum number of mines allowed to be placed by an entire team")]
        [ConfigHelp("Wormhole", "GravityBombs", ConfigScope.Arena, typeof(bool), "Whether a wormhole affects bombs")]
        [ConfigHelp("Bomb", "BombSafety", ConfigScope.Arena, typeof(bool), "Whether proximity bombs have a firing safety.  If enemy ship is within proximity radius, will it allow you to fire")]
        //Chat:MessageReliable
        [ConfigHelp("Prize", "TakePrizeReliable", ConfigScope.Arena, typeof(byte), "Whether prize packets are sent reliably (C2S)")]
        [ConfigHelp("Message", "AllowAudioMessages", ConfigScope.Arena, typeof(bool), "Whether players can send audio messages")]
        [ConfigHelp("Prize", "PrizeHideCount", ConfigScope.Arena, typeof(byte), "Number of prizes that are regenerated every PrizeDelay")]
        [ConfigHelp("Misc", "ExtraPositionData", ConfigScope.Arena, typeof(byte), "Whether regular players receive sysop data about a ship")]
        [ConfigHelp("Misc", "SlowFrameCheck", ConfigScope.Arena, typeof(byte), "Whether to check for slow frames on the client (possible cheat technique) (flawed on some machines, do not use)")]
        [ConfigHelp("Flag", "CarryFlags", ConfigScope.Arena, typeof(byte), "Whether the flags can be picked up and carried (0=no, 1=yes, 2=yes-one at a time, 3=yes-two at a time, 4=three, etc..)")]
        [ConfigHelp("Misc", "AllowSavedShips", ConfigScope.Arena, typeof(byte), "Whether saved ships are allowed (do not allow saved ship in zones where sub-arenas may have differing parameters)")]
        [ConfigHelp("Radar", "RadarMode", ConfigScope.Arena, typeof(byte), "Radar mode (0=normal, 1=half/half, 2=quarters, 3=half/half-see team mates, 4=quarters-see team mates)")]
        [ConfigHelp("Misc", "VictoryMusic", ConfigScope.Arena, typeof(byte), "Whether the zone plays victory music or not")]
        [ConfigHelp("Flag", "FlaggerGunUpgrade", ConfigScope.Arena, typeof(bool), "Whether the flaggers get a gun upgrade")]
        [ConfigHelp("Flag", "FlaggerBombUpgrade", ConfigScope.Arena, typeof(bool), "Whether the flaggers get a bomb upgrade")]
        [ConfigHelp("Soccer", "UseFlagger", ConfigScope.Arena, typeof(bool), "If player with soccer ball should use the Flag:Flagger* ship adjustments or not")]
        [ConfigHelp("Soccer", "BallLocation", ConfigScope.Arena, typeof(bool), "Whether the balls location is displayed at all times or not")]
        [ConfigHelp("Misc", "AntiWarpSettleDelay", ConfigScope.Arena, typeof(byte), "How many ticks to activate a fake antiwarp after attaching, portaling, or warping.")]
        public static readonly (string Section, string Key)[] ByteNames =
        {
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
        };

        [ConfigHelp("PrizeWeight", "QuickCharge", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Recharge' prize appearing")]
        [ConfigHelp("PrizeWeight", "Energy", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Energy Upgrade' prize appearing")]
        [ConfigHelp("PrizeWeight", "Rotation", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Rotation' prize appearing")]
        [ConfigHelp("PrizeWeight", "Stealth", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Stealth' prize appearing")]
        [ConfigHelp("PrizeWeight", "Cloak", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Cloak' prize appearing")]
        [ConfigHelp("PrizeWeight", "XRadar", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'XRadar' prize appearing")]
        [ConfigHelp("PrizeWeight", "Warp", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Warp' prize appearing")]
        [ConfigHelp("PrizeWeight", "Gun", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Gun Upgrade' prize appearing")]
        [ConfigHelp("PrizeWeight", "Bomb", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Bomb Upgrade' prize appearing")]
        [ConfigHelp("PrizeWeight", "BouncingBullets", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Bouncing Bullets' prize appearing")]
        [ConfigHelp("PrizeWeight", "Thruster", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Thruster' prize appearing")]
        [ConfigHelp("PrizeWeight", "TopSpeed", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Speed' prize appearing")]
        [ConfigHelp("PrizeWeight", "Recharge", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Full Charge' prize appearing")]
        [ConfigHelp("PrizeWeight", "Glue", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Engine Shutdown' prize appearing")]
        [ConfigHelp("PrizeWeight", "MultiFire", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'MultiFire' prize appearing")]
        [ConfigHelp("PrizeWeight", "Proximity", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Proximity Bomb' prize appearing")]
        [ConfigHelp("PrizeWeight", "AllWeapons", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Super!' prize appearing")]
        [ConfigHelp("PrizeWeight", "Shields", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Shields' prize appearing")]
        [ConfigHelp("PrizeWeight", "Shrapnel", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Shrapnel Upgrade' prize appearing")]
        [ConfigHelp("PrizeWeight", "AntiWarp", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'AntiWarp' prize appearing")]
        [ConfigHelp("PrizeWeight", "Repel", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Repel' prize appearing")]
        [ConfigHelp("PrizeWeight", "Burst", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Burst' prize appearing")]
        [ConfigHelp("PrizeWeight", "Decoy", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Decoy' prize appearing")]
        [ConfigHelp("PrizeWeight", "Thor", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Thor' prize appearing")]
        [ConfigHelp("PrizeWeight", "MultiPrize", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Multi-Prize' prize appearing")]
        [ConfigHelp("PrizeWeight", "Brick", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Brick' prize appearing")]
        [ConfigHelp("PrizeWeight", "Rocket", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Rocket' prize appearing")]
        [ConfigHelp("PrizeWeight", "Portal", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Portal' prize appearing")]
        public static readonly (string Section, string Key)[] PrizeWeightNames =
        {
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
        };

        [ConfigHelp("DPrizeWeight", "QuickCharge", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Recharge' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Energy", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Energy Upgrade' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Rotation", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Rotation' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Stealth", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Stealth' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Cloak", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Cloak' prize appearing")]
        [ConfigHelp("DPrizeWeight", "XRadar", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'XRadar' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Warp", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Warp' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Gun", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Gun Upgrade' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Bomb", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Bomb Upgrade' prize appearing")]
        [ConfigHelp("DPrizeWeight", "BouncingBullets", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Bouncing Bullets' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Thruster", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Thruster' prize appearing")]
        [ConfigHelp("DPrizeWeight", "TopSpeed", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Speed' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Recharge", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Full Charge' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Glue", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Engine Shutdown' prize appearing")]
        [ConfigHelp("DPrizeWeight", "MultiFire", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'MultiFire' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Proximity", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Proximity Bomb' prize appearing")]
        [ConfigHelp("DPrizeWeight", "AllWeapons", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Super!' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Shields", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Shields' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Shrapnel", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Shrapnel Upgrade' prize appearing")]
        [ConfigHelp("DPrizeWeight", "AntiWarp", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'AntiWarp' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Repel", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Repel' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Burst", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Burst' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Decoy", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Decoy' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Thor", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Thor' prize appearing")]
        [ConfigHelp("DPrizeWeight", "MultiPrize", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Multi-Prize' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Brick", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Brick' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Rocket", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Rocket' prize appearing")]
        [ConfigHelp("DPrizeWeight", "Portal", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "0-255", Description = "Likelihood of 'Portal' prize appearing")]
        public static readonly (string Section, string Key)[] DeathPrizeWeightNames =
        {
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
        };

        /* the following names are only key names, not key+section names */

        [ConfigHelp("All", "SuperTime", ConfigScope.Arena, typeof(int), "How long Super lasts on the ship (in ticks)")]
        [ConfigHelp("All", "ShieldsTime", ConfigScope.Arena, typeof(int), "How long Shields lasts on the ship (in ticks)")]
        public static readonly string[] ShipLongNames =
        {
            "SuperTime",
            "ShieldsTime"
        };

        [ConfigHelp("All", "Gravity", ConfigScope.Arena, typeof(short), "How strong of an effect the wormhole has on this ship (0 = none)")]
        [ConfigHelp("All", "GravityTopSpeed", ConfigScope.Arena, typeof(short), "Ship are allowed to move faster than their maximum speed while effected by a wormhole.  This determines how much faster they can go (0 = no extra speed)")]
        [ConfigHelp("All", "BulletFireEnergy", ConfigScope.Arena, typeof(short), "Amount of energy it takes a ship to fire a single L1 bullet")]
        [ConfigHelp("All", "MultiFireEnergy", ConfigScope.Arena, typeof(short), "Amount of energy it takes a ship to fire multifire L1 bullets")]
        [ConfigHelp("All", "BombFireEnergy", ConfigScope.Arena, typeof(short), "Amount of energy it takes a ship to fire a single bomb")]
        [ConfigHelp("All", "BombFireEnergyUpgrade", ConfigScope.Arena, typeof(short), "Extra amount of energy it takes a ship to fire an upgraded bomb. i.e. L2 = BombFireEnergy+BombFireEnergyUpgrade")]
        [ConfigHelp("All", "LandmineFireEnergy", ConfigScope.Arena, typeof(short), "Amount of energy it takes a ship to place a single L1 mine")]
        [ConfigHelp("All", "LandmineFireEnergyUpgrade", ConfigScope.Arena, typeof(short), "Extra amount of energy it takes to place an upgraded landmine. i.e. L2 = LandmineFireEnergy+LandmineFireEnergyUpgrade")]
        [ConfigHelp("All", "BulletSpeed", ConfigScope.Arena, typeof(short), "How fast bullets travel")]
        [ConfigHelp("All", "BombSpeed", ConfigScope.Arena, typeof(short), "How fast bombs travel")]
        [ConfigHelp("All", "MultiFireAngle", ConfigScope.Arena, typeof(short), "Angle spread between multi-fire bullets and standard forward firing bullets (111 = 1 degree, 1000 = 1 ship-rotation-point)")]
        [ConfigHelp("All", "CloakEnergy", ConfigScope.Arena, typeof(short), "Amount of energy required to have 'Cloak' activated (thousanths per tick)")]
        [ConfigHelp("All", "StealthEnergy", ConfigScope.Arena, typeof(short), "Amount of energy required to have 'Stealth' activated (thousanths per tick)")]
        [ConfigHelp("All", "AntiWarpEnergy", ConfigScope.Arena, typeof(short), "Amount of energy required to have 'Anti-Warp' activated (thousanths per tick)")]
        [ConfigHelp("All", "XRadarEnergy", ConfigScope.Arena, typeof(short), "Amount of energy required to have 'X-Radar' activated (thousanths per tick)")]
        [ConfigHelp("All", "MaximumRotation", ConfigScope.Arena, typeof(short), "Maximum rotation rate of the ship (0 = can't rotate, 400 = full rotation in 1 second)")]
        [ConfigHelp("All", "MaximumThrust", ConfigScope.Arena, typeof(short), "Maximum thrust of ship (0 = none)")]
        [ConfigHelp("All", "MaximumSpeed", ConfigScope.Arena, typeof(short), "Maximum speed of ship (0 = can't move)")]
        [ConfigHelp("All", "MaximumRecharge", ConfigScope.Arena, typeof(short), "Maximum recharge rate, or how quickly this ship recharges its energy")]
        [ConfigHelp("All", "MaximumEnergy", ConfigScope.Arena, typeof(short), "Maximum amount of energy that the ship can have")]
        [ConfigHelp("All", "InitialRotation", ConfigScope.Arena, typeof(short), "Initial rotation rate of the ship (0 = can't rotate, 400 = full rotation in 1 second)")]
        [ConfigHelp("All", "InitialThrust", ConfigScope.Arena, typeof(short), "Initial thrust of ship (0 = none)")]
        [ConfigHelp("All", "InitialSpeed", ConfigScope.Arena, typeof(short), "Initial speed of ship (0 = can't move)")]
        [ConfigHelp("All", "InitialRecharge", ConfigScope.Arena, typeof(short), "Initial recharge rate, or how quickly this ship recharges its energy")]
        [ConfigHelp("All", "InitialEnergy", ConfigScope.Arena, typeof(short), "Initial amount of energy that the ship can have")]
        [ConfigHelp("All", "UpgradeRotation", ConfigScope.Arena, typeof(short), "Amount added per 'Rotation' Prize")]
        [ConfigHelp("All", "UpgradeThrust", ConfigScope.Arena, typeof(short), "Amount added per 'Thruster' Prize")]
        [ConfigHelp("All", "UpgradeSpeed", ConfigScope.Arena, typeof(short), "Amount added per 'Speed' Prize")]
        [ConfigHelp("All", "UpgradeRecharge", ConfigScope.Arena, typeof(short), "Amount added per 'Recharge Rate' Prize")]
        [ConfigHelp("All", "UpgradeEnergy", ConfigScope.Arena, typeof(short), "Amount added per 'Energy Upgrade' Prize")]
        [ConfigHelp("All", "AfterburnerEnergy", ConfigScope.Arena, typeof(short), "Amount of energy required to have 'Afterburners' activated")]
        [ConfigHelp("All", "BombThrust", ConfigScope.Arena, typeof(short), "Amount of back-thrust you receive when firing a bomb")]
        [ConfigHelp("All", "BurstSpeed", ConfigScope.Arena, typeof(short), "How fast the burst shrapnel is for this ship")]
        [ConfigHelp("All", "TurretThrustPenalty", ConfigScope.Arena, typeof(short), "Amount the ship's thrust is decreased with a turret riding")]
        [ConfigHelp("All", "TurretSpeedPenalty", ConfigScope.Arena, typeof(short), "Amount the ship's speed is decreased with a turret riding")]
        [ConfigHelp("All", "BulletFireDelay", ConfigScope.Arena, typeof(short), "Delay that ship waits after a bullet is fired until another weapon may be fired (in ticks)")]
        [ConfigHelp("All", "MultiFireDelay", ConfigScope.Arena, typeof(short), "Delay that ship waits after a multifire bullet is fired until another weapon may be fired (in ticks)")]
        [ConfigHelp("All", "BombFireDelay", ConfigScope.Arena, typeof(short), "Delay that ship waits after a bomb is fired until another weapon may be fired (in ticks)")]
        [ConfigHelp("All", "LandmineFireDelay", ConfigScope.Arena, typeof(short), "Delay that ship waits after a mine is fired until another weapon may be fired (in ticks)")]
        [ConfigHelp("All", "RocketTime", ConfigScope.Arena, typeof(short), "How long a Rocket lasts (in ticks)")]
        [ConfigHelp("All", "InitialBounty", ConfigScope.Arena, typeof(short), "Number of 'Greens' given to ships when they start")]
        [ConfigHelp("All", "DamageFactor", ConfigScope.Arena, typeof(short), "How likely a the ship is to take damamage (ie. lose a prize) (0=special-case-never, 1=extremely likely, 5000=almost never)")]
        [ConfigHelp("All", "PrizeShareLimit", ConfigScope.Arena, typeof(short), "Maximum bounty that ships receive Team Prizes")]
        [ConfigHelp("All", "AttachBounty", ConfigScope.Arena, typeof(short), "Bounty required by ships to attach as a turret")]
        [ConfigHelp("All", "SoccerThrowTime", ConfigScope.Arena, typeof(short), "Time player has to carry soccer ball (in ticks)")]
        [ConfigHelp("All", "SoccerBallFriction", ConfigScope.Arena, typeof(short), "Amount the friction on the soccer ball (how quickly it slows down -- higher numbers mean faster slowdown)")]
        [ConfigHelp("All", "SoccerBallProximity", ConfigScope.Arena, typeof(short), "How close the player must be in order to pick up ball (in pixels)")]
        [ConfigHelp("All", "SoccerBallSpeed", ConfigScope.Arena, typeof(short), "Initial speed given to the ball when fired by the carrier")]
        public static readonly string[] ShipShortNames =
        {
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
        };

        [ConfigHelp("All", "TurretLimit", ConfigScope.Arena, typeof(byte), "Number of turrets allowed on a ship")]
        [ConfigHelp("All", "BurstShrapnel", ConfigScope.Arena, typeof(byte), "Number of bullets released when a 'Burst' is activated")]
        [ConfigHelp("All", "MaxMines", ConfigScope.Arena, typeof(byte), "Maximum number of mines allowed in ships")]
        [ConfigHelp("All", "RepelMax", ConfigScope.Arena, typeof(byte), "Maximum number of Repels allowed in ships")]
        [ConfigHelp("All", "BurstMax", ConfigScope.Arena, typeof(byte), "Maximum number of Bursts allowed in ships")]
        [ConfigHelp("All", "DecoyMax", ConfigScope.Arena, typeof(byte), "Maximum number of Decoys allowed in ships")]
        [ConfigHelp("All", "ThorMax", ConfigScope.Arena, typeof(byte), "Maximum number of Thor's Hammers allowed in ships")]
        [ConfigHelp("All", "BrickMax", ConfigScope.Arena, typeof(byte), "Maximum number of Bricks allowed in ships")]
        [ConfigHelp("All", "RocketMax", ConfigScope.Arena, typeof(byte), "Maximum number of Rockets allowed in ships")]
        [ConfigHelp("All", "PortalMax", ConfigScope.Arena, typeof(byte), "Maximum number of Portals allowed in ships")]
        [ConfigHelp("All", "InitialRepel", ConfigScope.Arena, typeof(byte), "Initial number of Repels given to ships when they start")]
        [ConfigHelp("All", "InitialBurst", ConfigScope.Arena, typeof(byte), "Initial number of Bursts given to ships when they start")]
        [ConfigHelp("All", "InitialBrick", ConfigScope.Arena, typeof(byte), "Initial number of Bricks given to ships when they start")]
        [ConfigHelp("All", "InitialRocket", ConfigScope.Arena, typeof(byte), "Initial number of Rockets given to ships when they start")]
        [ConfigHelp("All", "InitialThor", ConfigScope.Arena, typeof(byte), "Initial number of Thor's Hammers given to ships when they start")]
        [ConfigHelp("All", "InitialDecoy", ConfigScope.Arena, typeof(byte), "Initial number of Decoys given to ships when they start")]
        [ConfigHelp("All", "InitialPortal", ConfigScope.Arena, typeof(byte), "Initial number of Portals given to ships when they start")]
        [ConfigHelp("All", "BombBounceCount", ConfigScope.Arena, typeof(byte), "Number of times a ship's bombs bounce before they explode on impact")]
        public static readonly string[] ShipByteNames =
        {
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
        };
    }
}

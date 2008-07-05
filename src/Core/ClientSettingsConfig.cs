using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Core
{
    /// <summary>
    /// this file contains the config setting names for the various
    /// parameters sent to the client in the client settings packet
    /// 
    /// (blatent rip from ASSS)
    /// </summary>
    public static class ClientSettingsConfig
    {
        public static readonly string[] ShipNames = 
        {
            "Warbird",
	        "Javelin",
	        "Spider",
	        "Leviathan",
	        "Terrier",
	        "Weasel",
	        "Lancaster",
	        "Shark"
        };

        public static readonly string[] LongNames = 
        {
            /* cfghelp: Bullet:BulletDamageLevel, arena, int
	         * Maximum amount of damage that a L1 bullet will cause */
	        "Bullet:BulletDamageLevel", /* * 1000 */
	        /* cfghelp: Bomb:BombDamageLevel, arena, int
	         * Amount of damage a bomb causes at its center point (for all bomb
	         * levels) */
	        "Bomb:BombDamageLevel", /* * 1000 */
	        /* cfghelp: Bullet:BulletAliveTime, arena, int
	         * How long bullets live before disappearing (in ticks) */
	        "Bullet:BulletAliveTime",
	        /* cfghelp: Bomb:BombAliveTime, arena, int
	         * Time bomb is alive (in ticks) */
	        "Bomb:BombAliveTime",
	        /* cfghelp: Misc:DecoyAliveTime, arena, int
	         * Time a decoy is alive (in ticks) */
	        "Misc:DecoyAliveTime",
	        /* cfghelp: Misc:SafetyLimit, arena, int
	         * Amount of time that can be spent in the safe zone (in ticks) */
	        "Misc:SafetyLimit",
	        /* cfghelp: Misc:FrequencyShift, arena, int
	         * Amount of random frequency shift applied to sounds in the game */
	        "Misc:FrequencyShift",
	        "Team:MaxFrequency",
	        /* cfghelp: Repel:RepelSpeed, arena, int
	         * Speed at which players are repelled */
	        "Repel:RepelSpeed",
	        /* cfghelp: Mine:MineAliveTime, arena, int
	         * Time that mines are active (in ticks) */
	        "Mine:MineAliveTime",
	        /* cfghelp: Burst:BurstDamageLevel, arena, int
	         * Maximum amount of damage caused by a single burst bullet */
	        "Burst:BurstDamageLevel", /* * 1000 */
	        /* cfghelp: Bullet:BulletDamageUpgrade, arena, int
	         * Amount of extra damage each bullet level will cause */
	        "Bullet:BulletDamageUpgrade", /* * 1000 */
	        /* cfghelp: Flag:FlagDropDelay, arena, int
	         * Time before flag is dropped by carrier (0=never) */
	        "Flag:FlagDropDelay",
	        /* cfghelp: Flag:EnterGameFlaggingDelay, arena, int
	         * Time a new player must wait before they are allowed to see flags */
	        "Flag:EnterGameFlaggingDelay",
	        /* cfghelp: Rocket:RocketThrust, arena, int
	         * Thrust value given while a rocket is active */
	        "Rocket:RocketThrust",
	        /* cfghelp: Rocket:RocketSpeed, arena, int
	         * Speed value given while a rocket is active */
	        "Rocket:RocketSpeed",
	        /* cfghelp: Shrapnel:InactiveShrapDamage, arena, int
	         * Amount of damage shrapnel causes in it's first 1/4 second of
	         * life */
	        "Shrapnel:InactiveShrapDamage", /* * 1000 */
	        /* cfghelp: Wormhole:SwitchTime, arena, int
	         * How often the wormhole switches its destination */
	        "Wormhole:SwitchTime",
	        /* cfghelp: Misc:ActivateAppShutdownTime, arena, int
	         * Amount of time a ship is shutdown after application is
	         * reactivated */
	        "Misc:ActivateAppShutdownTime",
	        /* cfghelp: Shrapnel:ShrapnelSpeed, arena, int
	         * Speed that shrapnel travels */
	        "Shrapnel:ShrapnelSpeed",
        };

        public static readonly string[] ShortNames = 
        {
            	/* cfghelp: Latency:SendRoutePercent, arena, int
	 * Percentage of the ping time that is spent on the C2S portion of
	 * the ping (used in more accurately syncronizing clocks) */
	"Latency:SendRoutePercent",
	/* cfghelp: Bomb:BombExplodeDelay, arena, int
	 * How long after the proximity sensor is triggered before bomb
	 * explodes */
	"Bomb:BombExplodeDelay",
	/* cfghelp: Misc:SendPositionDelay, arena, int
	 * Amount of time between position packets sent by client */
	"Misc:SendPositionDelay",
	/* cfghelp: Bomb:BombExplodePixels, arena, int
	 * Blast radius in pixels for an L1 bomb (L2 bombs double this, L3
	 * bombs triple this) */
	"Bomb:BombExplodePixels",
	/* cfghelp: Prize:DeathPrizeTime, arena, int
	 * How long the prize exists that appears after killing somebody */
	"Prize:DeathPrizeTime",
	/* cfghelp: Bomb:JitterTime, arena, int
	 * How long the screen jitters from a bomb hit (in ticks) */
	"Bomb:JitterTime",
	/* cfghelp: Kill:EnterDelay, arena, int
	 * How long after a player dies before he can re-enter the game (in
	 * ticks) */
	"Kill:EnterDelay",
	/* cfghelp: Prize:EngineShutdownTime, arena, int
	 * Time the player is affected by an 'Engine Shutdown' Prize (in
	 * ticks) */
	"Prize:EngineShutdownTime",
	/* cfghelp: Bomb:ProximityDistance, arena, int
	 * Radius of proximity trigger in tiles (each bomb level adds 1 to
	 * this amount) */
	"Bomb:ProximityDistance",
	/* cfghelp: Kill:BountyIncreaseForKill, arena, int
	 * Number of points added to players bounty each time he kills an
	 * opponent */
	"Kill:BountyIncreaseForKill",
	/* cfghelp: Misc:BounceFactor, arena, int
	 * How bouncy the walls are (16 = no speed loss) */
	"Misc:BounceFactor",
	/* cfghelp: Radar:MapZoomFactor, arena, int
	 * A number representing how far you can see on radar */
	"Radar:MapZoomFactor",
	/* cfghelp: Kill:MaxBonus, arena, int
	 * FIXME: fill this in */
	"Kill:MaxBonus",
	/* cfghelp: Kill:MaxPenalty, arena, int
	 * FIXME: fill this in */
	"Kill:MaxPenalty",
	/* cfghelp: Kill:RewardBase, arena, int
	 * FIXME: fill this in */
	"Kill:RewardBase",
	/* cfghelp: Repel:RepelTime, arena, int
	 * Time players are affected by the repel (in ticks) */
	"Repel:RepelTime",
	/* cfghelp: Repel:RepelDistance, arena, int
	 * Number of pixels from the player that are affected by a repel */
	"Repel:RepelDistance",
	/* cfghelp: Misc:TickerDelay, arena, int
	 * Amount of time between ticker help messages */
	"Misc:TickerDelay",
	/* cfghelp: Flag:FlaggerOnRadar, arena, bool
	 * Whether the flaggers appear on radar in red */
	"Flag:FlaggerOnRadar",
	/* cfghelp: Flag:FlaggerKillMultiplier, arena, int
	 * Number of times more points are given to a flagger (1 = double
	 * points, 2 = triple points) */
	"Flag:FlaggerKillMultiplier",
	/* cfghelp: Prize:PrizeFactor, arena, int
	 * Number of prizes hidden is based on number of players in game.
	 * This number adjusts the formula, higher numbers mean more prizes.
	 * (Note: 10000 is max, 10 greens per person) */
	"Prize:PrizeFactor",
	/* cfghelp: Prize:PrizeDelay, arena, int
	 * How often prizes are regenerated (in ticks) */
	"Prize:PrizeDelay",
	/* cfghelp: Prize:MinimumVirtual, arena, int
	 * Distance from center of arena that prizes/flags/soccer-balls will
	 * spawn */
	"Prize:MinimumVirtual",
	/* cfghelp: Prize:UpgradeVirtual, arena, int
	 * Amount of additional distance added to MinimumVirtual for each
	 * player that is in the game */
	"Prize:UpgradeVirtual",
	/* cfghelp: Prize:PrizeMaxExist, arena, int
	 * Maximum amount of time that a hidden prize will remain on screen.
	 * (actual time is random) */
	"Prize:PrizeMaxExist",
	/* cfghelp: Prize:PrizeMinExist, arena, int
	 * Minimum amount of time that a hidden prize will remain on screen.
	 * (actual time is random) */
	"Prize:PrizeMinExist",
	/* cfghelp: Prize:PrizeNegativeFactor, arena, int
	 * Odds of getting a negative prize.  (1 = every prize, 32000 =
	 * extremely rare) */
	"Prize:PrizeNegativeFactor",
	/* cfghelp: Door:DoorDelay, arena, int
	 * How often doors attempt to switch their state */
	"Door:DoorDelay",
	/* cfghelp: Toggle:AntiWarpPixels, arena, int
	 * Distance Anti-Warp affects other players (in pixels) (note: enemy
	 * must also be on radar) */
	"Toggle:AntiWarpPixels",
	/* cfghelp: Door:DoorMode, arena, int
	 * Door mode (-2=all doors completely random, -1=weighted random
	 * (some doors open more often than others), 0-255=fixed doors (1
	 * bit of byte for each door specifying whether it is open or not) */
	"Door:DoorMode",
	/* cfghelp: Flag:FlagBlankDelay, arena, int
	 * Amount of time that a user can get no data from server before
	 * flags are hidden from view for 10 seconds */
	"Flag:FlagBlankDelay",
	/* cfghelp: Flag:NoDataFlagDropDelay, arena, int
	 * Amount of time that a user can get no data from server before
	 * flags he is carrying are dropped */
	"Flag:NoDataFlagDropDelay",
	/* cfghelp: Prize:MultiPrizeCount, arena, int
	 * Number of random greens given with a MultiPrize */
	"Prize:MultiPrizeCount",
	/* cfghelp: Brick:BrickTime, arena, int
	 * How long bricks last (in ticks) */
	"Brick:BrickTime",
	/* cfghelp: Misc:WarpRadiusLimit, arena, int
	 * When ships are randomly placed in the arena, this parameter will
	 * limit how far from the center of the arena they can be placed
	 * (1024=anywhere) */
	"Misc:WarpRadiusLimit",
	/* cfghelp: Bomb:EBombShutdownTime, arena, int
	 * Maximum time recharge is stopped on players hit with an EMP bomb */
	"Bomb:EBombShutdownTime",
	/* cfghelp: Bomb:EBombDamagePercent, arena, int
	 * Percentage of normal damage applied to an EMP bomb (in 0.1%) */
	"Bomb:EBombDamagePercent",
	/* cfghelp: Radar:RadarNeutralSize, arena, int
	 * Size of area between blinded radar zones (in pixels) */
	"Radar:RadarNeutralSize",
	/* cfghelp: Misc:WarpPointDelay, arena, int
	 * How long a portal is active */
	"Misc:WarpPointDelay",
	/* cfghelp: Misc:NearDeathLevel, arena, int
	 * Amount of energy that constitutes a near-death experience (ships
	 * bounty will be decreased by 1 when this occurs -- used for
	 * dueling zone) */
	"Misc:NearDeathLevel",
	/* cfghelp: Bomb:BBombDamagePercent, arena, int
	 * Percentage of normal damage applied to a bouncing bomb (in 0.1%) */
	"Bomb:BBombDamagePercent",
	/* cfghelp: Shrapnel:ShrapnelDamagePercent, arena, int
	 * Percentage of normal damage applied to shrapnel (relative to
	 * bullets of same level) (in 0.1%) */
	"Shrapnel:ShrapnelDamagePercent",
	/* cfghelp: Latency:ClientSlowPacketTime, arena, int
	 * Amount of latency S2C that constitutes a slow packet */
	"Latency:ClientSlowPacketTime",
	/* cfghelp: Flag:FlagDropResetReward, arena, int
	 * Minimum kill reward that a player must get in order to have his
	 * flag drop timer reset */
	"Flag:FlagDropResetReward",
	/* cfghelp: Flag:FlaggerFireCostPercent, arena, int
	 * Percentage of normal weapon firing cost for flaggers (in 0.1%) */
	"Flag:FlaggerFireCostPercent",
	/* cfghelp: Flag:FlaggerDamagePercent, arena, int
	 * Percentage of normal damage received by flaggers (in 0.1%) */
	"Flag:FlaggerDamagePercent",
	/* cfghelp: Flag:FlaggerBombFireDelay, arena, int
	 * Delay given to flaggers for firing bombs (zero is ships normal
	 * firing rate) (do not set this number less than 20) */
	"Flag:FlaggerBombFireDelay",
	/* cfghelp: Soccer:PassDelay, arena, int
	 * How long after the ball is fired before anybody can pick it up
	 * (in ticks) */
	"Soccer:PassDelay",
	/* cfghelp: Soccer:BallBlankDelay, arena, int
	 * Amount of time a player can receive no data from server and still
	 * pick up the soccer ball */
	"Soccer:BallBlankDelay",
	/* cfghelp: Latency:S2CNoDataKickoutDelay, arena, int
	 * Amount of time a user can receive no data from server before
	 * connection is terminated */
	"Latency:S2CNoDataKickoutDelay",
	/* cfghelp: Flag:FlaggerThrustAdjustment, arena, int
	 * Amount of thrust adjustment player carrying flag gets (negative
	 * numbers mean less thrust) */
	"Flag:FlaggerThrustAdjustment",
	/* cfghelp: Flag:FlaggerSpeedAdjustment, arena, int
	 * Amount of speed adjustment player carrying flag gets (negative
	 * numbers mean slower) */
	"Flag:FlaggerSpeedAdjustment",
	/* cfghelp: Latency:ClientSlowPacketSampleSize, arena, int
	 * Number of packets to sample S2C before checking for kickout */
	"Latency:ClientSlowPacketSampleSize",
	"Unused:Unused5",
	"Unused:Unused4",
	"Unused:Unused3",
	"Unused:Unused2",
	"Unused:Unused1"
        };

        public static string[] ByteNames = 
        {
            /* cfghelp: Shrapnel:Random, arena, bool
	 * Whether shrapnel spreads in circular or random patterns */
	"Shrapnel:Random",
	/* cfghelp: Soccer:BallBounce, arena, bool
	 * Whether the ball bounces off walls */
	"Soccer:BallBounce",
	/* cfghelp: Soccer:AllowBombs, arena, bool
	 * Whether the ball carrier can fire his bombs */
	"Soccer:AllowBombs",
	/* cfghelp: Soccer:AllowGuns, arena, bool
	 * Whether the ball carrier can fire his guns */
	"Soccer:AllowGuns",
	/* cfghelp: Soccer:Mode, arena, enum
	 * Goal configuration ($GOAL_ALL, $GOAL_LEFTRIGHT, $GOAL_TOPBOTTOM,
	 * $GOAL_CORNERS_3_1, $GOAL_CORNERS_1_3, $GOAL_SIDES_3_1,
	 * $GOAL_SIDES_1_3) */
	"Soccer:Mode",
	"Team:MaxPerTeam",
	"Team:MaxPerPrivateTeam",
	/* cfghelp: Mine:TeamMaxMines, arena, int
	 * Maximum number of mines allowed to be placed by an entire team */
	"Mine:TeamMaxMines",
	/* cfghelp: Wormhole:GravityBombs, arena, bool
	 * Whether a wormhole affects bombs */
	"Wormhole:GravityBombs",
	/* cfghelp: Bomb:BombSafety, arena, bool
	 * Whether proximity bombs have a firing safety.  If enemy ship is
	 * within proximity radius, will it allow you to fire */
	"Bomb:BombSafety",
	"Chat:MessageReliable",
	/* cfghelp: Prize:TakePrizeReliable, arena, int
	 * Whether prize packets are sent reliably (C2S) */
	"Prize:TakePrizeReliable",
	/* cfghelp: Message:AllowAudioMessages, arena, bool
	 * Whether players can send audio messages */
	"Message:AllowAudioMessages",
	/* cfghelp: Prize:PrizeHideCount, arena, int
	 * Number of prizes that are regenerated every PrizeDelay */
	"Prize:PrizeHideCount",
	/* cfghelp: Misc:ExtraPositionData, arena, int
	 * Whether regular players receive sysop data about a ship */
	"Misc:ExtraPositionData",
	/* cfghelp: Misc:SlowFrameCheck, arena, int
	 * Whether to check for slow frames on the client (possible cheat
	 * technique) (flawed on some machines, do not use) */
	"Misc:SlowFrameCheck",
	/* cfghelp: Flag:CarryFlags, arena, int
	 * Whether the flags can be picked up and carried (0=no, 1=yes,
	 * 2=yes-one at a time, 3=yes-two at a time, 4=three, etc..) */
	"Flag:CarryFlags",
	/* cfghelp: Misc:AllowSavedShips, arena, int
	 * Whether saved ships are allowed (do not allow saved ship in zones
	 * where sub-arenas may have differing parameters) */
	"Misc:AllowSavedShips",
	/* cfghelp: Radar:RadarMode, arena, int
	 * Radar mode (0=normal, 1=half/half, 2=quarters, 3=half/half-see
	 * team mates, 4=quarters-see team mates) */
	"Radar:RadarMode",
	/* cfghelp: Misc:VictoryMusic, arena, int
	 * Whether the zone plays victory music or not */
	"Misc:VictoryMusic",
	/* cfghelp: Flag:FlaggerGunUpgrade, arena, bool
	 * Whether the flaggers get a gun upgrade */
	"Flag:FlaggerGunUpgrade",
	/* cfghelp: Flag:FlaggerBombUpgrade, arena, bool
	 * Whether the flaggers get a bomb upgrade */
	"Flag:FlaggerBombUpgrade",
	/* cfghelp: Soccer:UseFlagger, arena, bool
	 * If player with soccer ball should use the Flag:Flagger* ship
	 * adjustments or not */
	"Soccer:UseFlagger",
	/* cfghelp: Soccer:BallLocation, arena, bool
	 * Whether the balls location is displayed at all times or not */
	"Soccer:BallLocation",
	/* cfghelp: Misc:AntiWarpSettleDelay, arena, int
	 * How many ticks to activate a fake antiwarp after attaching,
	 * portaling, or warping. */
	"Misc:AntiWarpSettleDelay",
	"Unused:Unused7",
	"Unused:Unused6",
	"Unused:Unused5",
	"Unused:Unused4",
	"Unused:Unused3",
	"Unused:Unused2",
	"Unused:Unused1"
        };

        public static readonly string[] PrizeweightNames = 
        {
            /* cfghelp: PrizeWeight:QuickCharge, arena, int
	 * Likelihood of 'Recharge' prize appearing */
	"PrizeWeight:QuickCharge",
	/* cfghelp: PrizeWeight:Energy, arena, int
	 * Likelihood of 'Energy Upgrade' prize appearing */
	"PrizeWeight:Energy",
	/* cfghelp: PrizeWeight:Rotation, arena, int
	 * Likelihood of 'Rotation' prize appearing */
	"PrizeWeight:Rotation",
	/* cfghelp: PrizeWeight:Stealth, arena, int
	 * Likelihood of 'Stealth' prize appearing */
	"PrizeWeight:Stealth",
	/* cfghelp: PrizeWeight:Cloak, arena, int
	 * Likelihood of 'Cloak' prize appearing */
	"PrizeWeight:Cloak",
	/* cfghelp: PrizeWeight:XRadar, arena, int
	 * Likelihood of 'XRadar' prize appearing */
	"PrizeWeight:XRadar",
	/* cfghelp: PrizeWeight:Warp, arena, int
	 * Likelihood of 'Warp' prize appearing */
	"PrizeWeight:Warp",
	/* cfghelp: PrizeWeight:Gun, arena, int
	 * Likelihood of 'Gun Upgrade' prize appearing */
	"PrizeWeight:Gun",
	/* cfghelp: PrizeWeight:Bomb, arena, int
	 * Likelihood of 'Bomb Upgrade' prize appearing */
	"PrizeWeight:Bomb",
	/* cfghelp: PrizeWeight:BouncingBullets, arena, int
	 * Likelihood of 'Bouncing Bullets' prize appearing */
	"PrizeWeight:BouncingBullets",
	/* cfghelp: PrizeWeight:Thruster, arena, int
	 * Likelihood of 'Thruster' prize appearing */
	"PrizeWeight:Thruster",
	/* cfghelp: PrizeWeight:TopSpeed, arena, int
	 * Likelihood of 'Speed' prize appearing */
	"PrizeWeight:TopSpeed",
	/* cfghelp: PrizeWeight:Recharge, arena, int
	 * Likelihood of 'Full Charge' prize appearing (not 'Recharge') */
	"PrizeWeight:Recharge",
	/* cfghelp: PrizeWeight:Glue, arena, int
	 * Likelihood of 'Engine Shutdown' prize appearing */
	"PrizeWeight:Glue",
	/* cfghelp: PrizeWeight:MultiFire, arena, int
	 * Likelihood of 'MultiFire' prize appearing */
	"PrizeWeight:MultiFire",
	/* cfghelp: PrizeWeight:Proximity, arena, int
	 * Likelihood of 'Proximity Bomb' prize appearing */
	"PrizeWeight:Proximity",
	/* cfghelp: PrizeWeight:AllWeapons, arena, int
	 * Likelihood of 'Super!' prize appearing */
	"PrizeWeight:AllWeapons",
	/* cfghelp: PrizeWeight:Shields, arena, int
	 * Likelihood of 'Shields' prize appearing */
	"PrizeWeight:Shields",
	/* cfghelp: PrizeWeight:Shrapnel, arena, int
	 * Likelihood of 'Shrapnel Upgrade' prize appearing */
	"PrizeWeight:Shrapnel",
	/* cfghelp: PrizeWeight:AntiWarp, arena, int
	 * Likelihood of 'AntiWarp' prize appearing */
	"PrizeWeight:AntiWarp",
	/* cfghelp: PrizeWeight:Repel, arena, int
	 * Likelihood of 'Repel' prize appearing */
	"PrizeWeight:Repel",
	/* cfghelp: PrizeWeight:Burst, arena, int
	 * Likelihood of 'Burst' prize appearing */
	"PrizeWeight:Burst",
	/* cfghelp: PrizeWeight:Decoy, arena, int
	 * Likelihood of 'Decoy' prize appearing */
	"PrizeWeight:Decoy",
	/* cfghelp: PrizeWeight:Thor, arena, int
	 * Likelihood of 'Thor' prize appearing */
	"PrizeWeight:Thor",
	/* cfghelp: PrizeWeight:MultiPrize, arena, int
	 * Likelihood of 'Multi-Prize' prize appearing */
	"PrizeWeight:MultiPrize",
	/* cfghelp: PrizeWeight:Brick, arena, int
	 * Likelihood of 'Brick' prize appearing */
	"PrizeWeight:Brick",
	/* cfghelp: PrizeWeight:Rocket, arena, int
	 * Likelihood of 'Rocket' prize appearing */
	"PrizeWeight:Rocket",
	/* cfghelp: PrizeWeight:Portal, arena, int
	 * Likelihood of 'Portal' prize appearing */
	"PrizeWeight:Portal"
        };

        /* the following names are only key names, not key+section names */

        public static readonly string[] ShipLongNames = 
        {
            /* cfghelp: All:SuperTime, arena, int
	 * How long Super lasts on the ship (in ticks) */
	"SuperTime",
	/* cfghelp: All:ShieldsTime, arena, int
	 * How long Shields lasts on the ship (in ticks)
	 * */
	"ShieldsTime"
        };

        public static readonly string[] ShipShortNames = 
        {
            /* cfghelp: All:Gravity, arena, int
	 * How strong of an effect the wormhole has on this ship (0 = none) */
	"Gravity",
	/* cfghelp: All:GravityTopSpeed, arena, int
	 * Ship are allowed to move faster than their maximum speed while
	 * effected by a wormhole.  This determines how much faster they can
	 * go (0 = no extra speed) */
	"GravityTopSpeed",
	/* cfghelp: All:BulletFireEnergy, arena, int
	 * Amount of energy it takes a ship to fire a single L1 bullet */
	"BulletFireEnergy",
	/* cfghelp: All:MultiFireEnergy, arena, int
	 * Amount of energy it takes a ship to fire multifire L1 bullets */
	"MultiFireEnergy",
	/* cfghelp: All:BombFireEnergy, arena, int
	 * Amount of energy it takes a ship to fire a single bomb */
	"BombFireEnergy",
	/* cfghelp: All:BombFireEnergyUpgrade, arena, int
	 * Extra amount of energy it takes a ship to fire an upgraded bomb.
	 * i.e. L2 = BombFireEnergy+BombFireEnergyUpgrade */
	"BombFireEnergyUpgrade",
	/* cfghelp: All:LandmineFireEnergy, arena, int
	 * Amount of energy it takes a ship to place a single L1 mine */
	"LandmineFireEnergy",
	/* cfghelp: All:LandmineFireEnergyUpgrade, arena, int
	 * Extra amount of energy it takes to place an upgraded landmine.
	 * i.e. L2 = LandmineFireEnergy+LandmineFireEnergyUpgrade */
	"LandmineFireEnergyUpgrade",
	/* cfghelp: All:BulletSpeed, arena, int
	 * How fast bullets travel */
	"BulletSpeed",
	/* cfghelp: All:BombSpeed, arena, int
	 * How fast bombs travel */
	"BombSpeed",

	/* the next three settings are in this bitfield: */
	/* cfghelp: All:SeeBombLevel, arena, int, range: 0-4
	 * If ship can see bombs on radar (0=Disabled, 1=All, 2=L2 and up,
	 * 3=L3 and up, 4=L4 bombs only) */
	/* cfghelp: All:DisableFastShooting, arena, bool
	 * If firing bullets, bombs, or thors is disabled after using
	 * afterburners (1=enabled) (Cont .36+) */
	/* cfghelp: All:Radius, arena, int, range: 0-255, def: 14
	 * The ship's radius from center to outside, in pixels. (Cont .37+) */
	"___MiscBitfield___",

	/* cfghelp: All:MultiFireAngle, arena, int
	 * Angle spread between multi-fire bullets and standard forward
	 * firing bullets (111 = 1 degree, 1000 = 1 ship-rotation-point) */
	"MultiFireAngle",
	/* cfghelp: All:CloakEnergy, arena, int
	 * Amount of energy required to have 'Cloak' activated (thousanths
	 * per tick) */
	"CloakEnergy",
	/* cfghelp: All:StealthEnergy, arena, int
	 * Amount of energy required to have 'Stealth' activated (thousanths
	 * per tick) */
	"StealthEnergy",
	/* cfghelp: All:AntiWarpEnergy, arena, int
	 * Amount of energy required to have 'Anti-Warp' activated
	 * (thousanths per tick) */
	"AntiWarpEnergy",
	/* cfghelp: All:XRadarEnergy, arena, int
	 * Amount of energy required to have 'X-Radar' activated (thousanths
	 * per tick) */
	"XRadarEnergy",
	/* cfghelp: All:MaximumRotation, arena, int
	 * Maximum rotation rate of the ship (0 = can't rotate, 400 = full
	 * rotation in 1 second) */
	"MaximumRotation",
	/* cfghelp: All:MaximumThrust, arena, int
	 * Maximum thrust of ship (0 = none) */
	"MaximumThrust",
	/* cfghelp: All:MaximumSpeed, arena, int
	 * Maximum speed of ship (0 = can't move) */
	"MaximumSpeed",
	/* cfghelp: All:MaximumRecharge, arena, int
	 * Maximum recharge rate, or how quickly this ship recharges its
	 * energy */
	"MaximumRecharge",
	/* cfghelp: All:MaximumEnergy, arena, int
	 * Maximum amount of energy that the ship can have */
	"MaximumEnergy",
	/* cfghelp: All:InitialRotation, arena, int
	 * Initial rotation rate of the ship (0 = can't rotate, 400 = full
	 * rotation in 1 second) */
	"InitialRotation",
	/* cfghelp: All:InitialThrust, arena, int
	 * Initial thrust of ship (0 = none) */
	"InitialThrust",
	/* cfghelp: All:InitialSpeed, arena, int
	 * Initial speed of ship (0 = can't move) */
	"InitialSpeed",
	/* cfghelp: All:InitialRecharge, arena, int
	 * Initial recharge rate, or how quickly this ship recharges its
	 * energy */
	"InitialRecharge",
	/* cfghelp: All:InitialEnergy, arena, int
	 * Initial amount of energy that the ship can have */
	"InitialEnergy",
	/* cfghelp: All:UpgradeRotation, arena, int
	 * Amount added per 'Rotation' Prize */
	"UpgradeRotation",
	/* cfghelp: All:UpgradeThrust, arena, int
	 * Amount added per 'Thruster' Prize */
	"UpgradeThrust",
	/* cfghelp: All:UpgradeSpeed, arena, int
	 * Amount added per 'Speed' Prize */
	"UpgradeSpeed",
	/* cfghelp: All:UpgradeRecharge, arena, int
	 * Amount added per 'Recharge Rate' Prize */
	"UpgradeRecharge",
	/* cfghelp: All:UpgradeEnergy, arena, int
	 * Amount added per 'Energy Upgrade' Prize */
	"UpgradeEnergy",
	/* cfghelp: All:AfterburnerEnergy, arena, int
	 * Amount of energy required to have 'Afterburners' activated */
	"AfterburnerEnergy",
	/* cfghelp: All:BombThrust, arena, int
	 * Amount of back-thrust you receive when firing a bomb */
	"BombThrust",
	/* cfghelp: All:BurstSpeed, arena, int
	 * How fast the burst shrapnel is for this ship */
	"BurstSpeed",
	/* cfghelp: All:TurretThrustPenalty, arena, int
	 * Amount the ship's thrust is decreased with a turret riding */
	"TurretThrustPenalty",
	/* cfghelp: All:TurretSpeedPenalty, arena, int
	 * Amount the ship's speed is decreased with a turret riding */
	"TurretSpeedPenalty",
	/* cfghelp: All:BulletFireDelay, arena, int
	 * Delay that ship waits after a bullet is fired until another
	 * weapon may be fired (in ticks) */
	"BulletFireDelay",
	/* cfghelp: All:MultiFireDelay, arena, int
	 * Delay that ship waits after a multifire bullet is fired until
	 * another weapon may be fired (in ticks) */
	"MultiFireDelay",
	/* cfghelp: All:BombFireDelay, arena, int
	 * delay that ship waits after a bomb is fired until another weapon
	 * may be fired (in ticks) */
	"BombFireDelay",
	/* cfghelp: All:LandmineFireDelay, arena, int
	 * Delay that ship waits after a mine is fired until another weapon
	 * may be fired (in ticks) */
	"LandmineFireDelay",
	/* cfghelp: All:RocketTime, arena, int
	 * How long a Rocket lasts (in ticks) */
	"RocketTime",
	/* cfghelp: All:InitialBounty, arena, int
	 * Number of 'Greens' given to ships when they start */
	"InitialBounty",
	/* cfghelp: All:DamageFactor, arena, int
	 * How likely a the ship is to take damamage (ie. lose a prize)
	 * (0=special-case-never, 1=extremely likely, 5000=almost never) */
	"DamageFactor",
	/* cfghelp: All:PrizeShareLimit, arena, int
	 * Maximum bounty that ships receive Team Prizes */
	"PrizeShareLimit",
	/* cfghelp: All:AttachBounty, arena, int
	 * Bounty required by ships to attach as a turret */
	"AttachBounty",
	/* cfghelp: All:SoccerThrowTime, arena, int
	 * Time player has to carry soccer ball (in ticks) */
	"SoccerThrowTime",
	/* cfghelp: All:SoccerBallFriction, arena, int
	 * Amount the friction on the soccer ball (how quickly it slows down
	 * -- higher numbers mean faster slowdown) */
	"SoccerBallFriction",
	/* cfghelp: All:SoccerBallProximity, arena, int
	 * How close the player must be in order to pick up ball (in pixels) */
	"SoccerBallProximity",
	/* cfghelp: All:SoccerBallSpeed, arena, int
	 * Initial speed given to the ball when fired by the carrier */
	"SoccerBallSpeed"
        };

        public static readonly string[] ShipByteNames = 
        {
            /* cfghelp: All:TurretLimit, arena, int
	 * Number of turrets allowed on a ship */
	"TurretLimit",
	/* cfghelp: All:BurstShrapnel, arena, int
	 * Number of bullets released when a 'Burst' is activated */
	"BurstShrapnel",
	/* cfghelp: All:MaxMines, arena, int
	 * Maximum number of mines allowed in ships */
	"MaxMines",
	/* cfghelp: All:RepelMax, arena, int
	 * Maximum number of Repels allowed in ships */
	"RepelMax",
	/* cfghelp: All:BurstMax, arena, int
	 * Maximum number of Bursts allowed in ships */
	"BurstMax",
	/* cfghelp: All:DecoyMax, arena, int
	 * Maximum number of Decoys allowed in ships */
	"DecoyMax",
	/* cfghelp: All:ThorMax, arena, int
	 * Maximum number of Thor's Hammers allowed in ships */
	"ThorMax",
	/* cfghelp: All:BrickMax, arena, int
	 * Maximum number of Bricks allowed in ships */
	"BrickMax",
	/* cfghelp: All:RocketMax, arena, int
	 * Maximum number of Rockets allowed in ships */
	"RocketMax",
	/* cfghelp: All:PortalMax, arena, int
	 * Maximum number of Portals allowed in ships */
	"PortalMax",
	/* cfghelp: All:InitialRepel, arena, int
	 * Initial number of Repels given to ships when they start */
	"InitialRepel",
	/* cfghelp: All:InitialBurst, arena, int
	 * Initial number of Bursts given to ships when they start */
	"InitialBurst",
	/* cfghelp: All:InitialBrick, arena, int
	 * Initial number of Bricks given to ships when they start */
	"InitialBrick",
	/* cfghelp: All:InitialRocket, arena, int
	 * Initial number of Rockets given to ships when they start */
	"InitialRocket",
	/* cfghelp: All:InitialThor, arena, int
	 * Initial number of Thor's Hammers given to ships when they start */
	"InitialThor",
	/* cfghelp: All:InitialDecoy, arena, int
	 * Initial number of Decoys given to ships when they start */
	"InitialDecoy",
	/* cfghelp: All:InitialPortal, arena, int
	 * Initial number of Portals given to ships when they start */
	"InitialPortal",
	/* cfghelp: All:BombBounceCount, arena, int
	 * Number of times a ship's bombs bounce before they explode on
	 * impact */
	"BombBounceCount"
        };

        /* here's a bunch of documentation that's should go here: */

        /* here's a bunch of documentation that's should go here: */

        /* ship settings stuff */

        /* cfghelp: All:ShrapnelMax, arena, int
         * Maximum amount of shrapnel released from a ship's bomb */

        /* cfghelp: All:ShrapnelRate, arena, int
         * Amount of additional shrapnel gained by a 'Shrapnel Upgrade' prize. */

        /* cfghelp: All:AntiWarpStatus, arena, int, range: 0-2
         * Whether ships are allowed to receive 'Anti-Warp' 0=no 1=yes
         * 2=yes/start-with */

        /* cfghelp: All:CloakStatus, arena, int, range: 0-2
         * Whether ships are allowed to receive 'Cloak' 0=no 1=yes
         * 2=yes/start-with */

        /* cfghelp: All:StealthStatus, arena, int, range: 0-2
         * Whether ships are allowed to receive 'Stealth' 0=no 1=yes
         * 2=yes/start-with */

        /* cfghelp: All:XRadarStatus, arena, int, range: 0-2
         * Whether ships are allowed to receive 'X-Radar' 0=no 1=yes
         * 2=yes/start-with */

        /* cfghelp: All:InitialGuns, arena, int, range: 0-3
         * Initial level a ship's guns fire */

        /* cfghelp: All:MaxGuns, arena, int, range: 0-3
         * Maximum level a ship's guns can fire */

        /* cfghelp: All:InitialBombs, arena, range: 0-3
         * Initial level a ship's bombs fire */

        /* cfghelp: All:MaxBombs, arena, int, range: 0-3
         * Maximum level a ship's bombs can fire */

        /* cfghelp: All:DoubleBarrel, arena, bool
         * Whether ships fire with double barrel bullets */

        /* cfghelp: All:EmpBomb, arena, bool
         * Whether ships fire EMP bombs */

        /* cfghelp: All:SeeMines, arena, bool
         * Whether ships see mines on radar */


        /* extra arena settings */

        /* cfghelp: Bullet:ExactDamage, arena, bool, def: 0
         * Whether to use exact bullet damage (Cont .36+) */

        /* cfghelp: Spectator:HideFlags, arena, bool, def: 0
         * Whether to show dropped flags to spectators (Cont .36+) */

        /* cfghelp: Spectator:NoXRadar, arena, bool, def: 0
         * Whether spectators are disallowed from having X radar (Cont .36+) */

        /* cfghelp: Misc:DisableScreenshot, arena, bool, def: 0
         * Whether to disable Continuum's screenshot feature (Cont .37+) */

        /* FIXME: need cfghelp for Misc:SlowFrameRate, Misc:MaxTimerDrift */

        /* cfghelp: Soccer:DisableWallPass, arena, bool, def: 0
         * Whether to disable ball-passing through walls (Cont .38+) */

        /* cfghelp: Soccer:DisableBallKilling, arena, bool, def: 0
         * Whether to disable ball killing in safe zones (Cont .38+) */

        /* cfghelp: Spawn:TeamN-X/Y/Radius, arena, int
         * Specify spawn location and radius per team. If only Team0 variables
         * are set, all teams use them, if Team0 and Team1 variables are set,
         * even teams use Team0 and odd teams use Team1. It is possible to set
         * spawn positions for upto 4 teams (Team0-Team3). (Cont .38+) */
    }
}

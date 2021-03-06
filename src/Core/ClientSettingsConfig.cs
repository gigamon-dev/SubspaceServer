﻿using System;
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
		public static readonly string[] LongNames = 
        {
	        "Bullet:BulletDamageLevel", /* * 1000 */
	        "Bomb:BombDamageLevel", /* * 1000 */
	        "Bullet:BulletAliveTime",
	        "Bomb:BombAliveTime",
	        "Misc:DecoyAliveTime",
	        "Misc:SafetyLimit",
	        "Misc:FrequencyShift",
	        "Team:MaxFrequency",
	        "Repel:RepelSpeed",
	        "Mine:MineAliveTime",
	        "Burst:BurstDamageLevel", /* * 1000 */
	        "Bullet:BulletDamageUpgrade", /* * 1000 */
	        "Flag:FlagDropDelay",
	        "Flag:EnterGameFlaggingDelay",
	        "Rocket:RocketThrust",
	        "Rocket:RocketSpeed",
	        "Shrapnel:InactiveShrapDamage", /* * 1000 */
	        "Wormhole:SwitchTime",
	        "Misc:ActivateAppShutdownTime",
	        "Shrapnel:ShrapnelSpeed",
        };

		[ConfigHelp("Latency", "SendRoutePercent", ConfigScope.Arena, typeof(int), "Percentage of the ping time that is spent on the C2S portion of the ping (used in more accurately syncronizing clocks)")]
		[ConfigHelp("Bomb", "BombExplodeDelay", ConfigScope.Arena, typeof(int), "How long after the proximity sensor is triggered before bomb explodes")]
		[ConfigHelp("Misc", "SendPositionDelay", ConfigScope.Arena, typeof(int), "Amount of time between position packets sent by client")]
		[ConfigHelp("Bomb", "BombExplodePixels", ConfigScope.Arena, typeof(int), "Blast radius in pixels for an L1 bomb (L2 bombs double this, L3 bombs triple this)")]
		[ConfigHelp("Prize", "DeathPrizeTime", ConfigScope.Arena, typeof(int), "How long the prize exists that appears after killing somebody")]
		[ConfigHelp("Bomb", "JitterTime", ConfigScope.Arena, typeof(int), "How long the screen jitters from a bomb hit (in ticks)")]
		[ConfigHelp("Kill", "EnterDelay", ConfigScope.Arena, typeof(int), "How long after a player dies before he can re-enter the game (in ticks)")]
		[ConfigHelp("Prize", "EngineShutdownTime", ConfigScope.Arena, typeof(int), "Time the player is affected by an 'Engine Shutdown' Prize (in ticks)")]
		[ConfigHelp("Bomb", "ProximityDistance", ConfigScope.Arena, typeof(int), "Radius of proximity trigger in tiles (each bomb level adds 1 to this amount)")]
		[ConfigHelp("Kill", "BountyIncreaseForKill", ConfigScope.Arena, typeof(int), "Number of points added to players bounty each time he kills an opponent")]
		[ConfigHelp("Misc", "BounceFactor", ConfigScope.Arena, typeof(int), "How bouncy the walls are (16 = no speed loss)")]
		[ConfigHelp("Radar", "MapZoomFactor", ConfigScope.Arena, typeof(int), "A number representing how much the map is zoomed out for radar. (48 = whole map on radar, 49+ = effectively disable radar)")]
		[ConfigHelp("Kill", "MaxBonus", ConfigScope.Arena, typeof(int), "This is if you have flags, can add more points per a kill.")]
		[ConfigHelp("Kill", "MaxPenalty", ConfigScope.Arena, typeof(int), "This is if you have flags, can take away points per a kill.")]
		[ConfigHelp("Kill", "RewardBase", ConfigScope.Arena, typeof(int), "This is shown added to a person's bty, but isn't added from points for a kill.")]
		[ConfigHelp("Repel", "RepelTime", ConfigScope.Arena, typeof(int), "Time players are affected by the repel (in ticks)")]
		[ConfigHelp("Repel", "RepelDistance", ConfigScope.Arena, typeof(int), "Number of pixels from the player that are affected by a repel")]
		[ConfigHelp("Misc", "TickerDelay", ConfigScope.Arena, typeof(int), "Amount of time between ticker help messages")]
		[ConfigHelp("Flag", "FlaggerOnRadar", ConfigScope.Arena, typeof(bool), "Whether the flaggers appear on radar in red")]
		[ConfigHelp("Flag", "FlaggerKillMultiplier", ConfigScope.Arena, typeof(int), "Number of times more points are given to a flagger (1 = double points, 2 = triple points)")]
		[ConfigHelp("Prize", "PrizeFactor", ConfigScope.Arena, typeof(int), "Number of prizes hidden is based on number of players in game. This number adjusts the formula, higher numbers mean more prizes. (Note: 10000 is max, 10 greens per person)")]
		[ConfigHelp("Prize", "PrizeDelay", ConfigScope.Arena, typeof(int), "How often prizes are regenerated (in ticks)")]
		[ConfigHelp("Prize", "MinimumVirtual", ConfigScope.Arena, typeof(int), "Distance from center of arena that prizes/flags/soccer-balls will spawn")]
		[ConfigHelp("Prize", "UpgradeVirtual", ConfigScope.Arena, typeof(int), "Amount of additional distance added to MinimumVirtual for each player that is in the game")]
		[ConfigHelp("Prize", "PrizeMaxExist", ConfigScope.Arena, typeof(int), "Maximum amount of time that a hidden prize will remain on screen. (actual time is random)")]
		[ConfigHelp("Prize", "PrizeMinExist", ConfigScope.Arena, typeof(int), "Minimum amount of time that a hidden prize will remain on screen. (actual time is random)")]
		[ConfigHelp("Prize", "PrizeNegativeFactor", ConfigScope.Arena, typeof(int), "Odds of getting a negative prize.  (1 = every prize, 32000 = extremely rare)")]
		[ConfigHelp("Door", "DoorDelay", ConfigScope.Arena, typeof(int), "How often doors attempt to switch their state")]
		[ConfigHelp("Toggle", "AntiWarpPixels", ConfigScope.Arena, typeof(int), "Distance Anti-Warp affects other players (in pixels) (note: enemy must also be on radar)")]
		[ConfigHelp("Door", "DoorMode", ConfigScope.Arena, typeof(int), "Door mode (-2=all doors completely random, -1=weighted random (some doors open more often than others), 0-255=fixed doors (1 bit of byte for each door specifying whether it is open or not)")]
		[ConfigHelp("Flag", "FlagBlankDelay", ConfigScope.Arena, typeof(int), "Amount of time that a user can get no data from server before flags are hidden from view for 10 seconds")]
		[ConfigHelp("Flag", "NoDataFlagDropDelay", ConfigScope.Arena, typeof(int), "Amount of time that a user can get no data from server before flags he is carrying are dropped")]
		[ConfigHelp("Prize", "MultiPrizeCount", ConfigScope.Arena, typeof(int), "Number of random greens given with a MultiPrize")]
		[ConfigHelp("Brick", "BrickTime", ConfigScope.Arena, typeof(int), "How long bricks last (in ticks)")]
		[ConfigHelp("Misc", "WarpRadiusLimit", ConfigScope.Arena, typeof(int), "When ships are randomly placed in the arena, this parameter will limit how far from the center of the arena they can be placed (1024=anywhere)")]
		[ConfigHelp("Bomb", "EBombShutdownTime", ConfigScope.Arena, typeof(int), "Maximum time recharge is stopped on players hit with an EMP bomb")]
		[ConfigHelp("Bomb", "EBombDamagePercent", ConfigScope.Arena, typeof(int), "Percentage of normal damage applied to an EMP bomb (in 0.1%)")]
		[ConfigHelp("Radar", "RadarNeutralSize", ConfigScope.Arena, typeof(int), "Size of area between blinded radar zones (in pixels)")]
		[ConfigHelp("Misc", "WarpPointDelay", ConfigScope.Arena, typeof(int), "How long a portal is active")]
		[ConfigHelp("Misc", "NearDeathLevel", ConfigScope.Arena, typeof(int), "Amount of energy that constitutes a near-death experience (ships bounty will be decreased by 1 when this occurs -- used for dueling zone)")]
		[ConfigHelp("Bomb", "BBombDamagePercent", ConfigScope.Arena, typeof(int), "Percentage of normal damage applied to a bouncing bomb (in 0.1%)")]
		[ConfigHelp("Shrapnel", "ShrapnelDamagePercent", ConfigScope.Arena, typeof(int), "Percentage of normal damage applied to shrapnel (relative to bullets of same level) (in 0.1%)")]
		[ConfigHelp("Latency", "ClientSlowPacketTime", ConfigScope.Arena, typeof(int), "Amount of latency S2C that constitutes a slow packet")]
		[ConfigHelp("Flag", "FlagDropResetReward", ConfigScope.Arena, typeof(int), "Minimum kill reward that a player must get in order to have his flag drop timer reset")]
		[ConfigHelp("Flag", "FlaggerFireCostPercent", ConfigScope.Arena, typeof(int), "Percentage of normal weapon firing cost for flaggers (in 0.1%)")]
		[ConfigHelp("Flag", "FlaggerDamagePercent", ConfigScope.Arena, typeof(int), "Percentage of normal damage received by flaggers (in 0.1%)")]
		[ConfigHelp("Flag", "FlaggerBombFireDelay", ConfigScope.Arena, typeof(int), "Delay given to flaggers for firing bombs (zero is ships normal firing rate) (do not set this number less than 20)")]
		[ConfigHelp("Soccer", "PassDelay", ConfigScope.Arena, typeof(int), "How long after the ball is fired before anybody can pick it up (in ticks)")]
		[ConfigHelp("Soccer", "BallBlankDelay", ConfigScope.Arena, typeof(int), "Amount of time a player can receive no data from server and still pick up the soccer ball")]
		[ConfigHelp("Latency", "S2CNoDataKickoutDelay", ConfigScope.Arena, typeof(int), "Amount of time a user can receive no data from server before connection is terminated")]
		[ConfigHelp("Flag", "FlaggerThrustAdjustment", ConfigScope.Arena, typeof(int), "Amount of thrust adjustment player carrying flag gets (negative numbers mean less thrust)")]
		[ConfigHelp("Flag", "FlaggerSpeedAdjustment", ConfigScope.Arena, typeof(int), "Amount of speed adjustment player carrying flag gets (negative numbers mean slower)")]
		[ConfigHelp("Latency", "ClientSlowPacketSampleSize", ConfigScope.Arena, typeof(int), "Number of packets to sample S2C before checking for kickout")]
        public static readonly string[] ShortNames = 
        {
			"Latency:SendRoutePercent",
			"Bomb:BombExplodeDelay",
			"Misc:SendPositionDelay",
			"Bomb:BombExplodePixels",
			"Prize:DeathPrizeTime",
			"Bomb:JitterTime",
			"Kill:EnterDelay",
			"Prize:EngineShutdownTime",
			"Bomb:ProximityDistance",
			"Kill:BountyIncreaseForKill",
			"Misc:BounceFactor",
			"Radar:MapZoomFactor",
			"Kill:MaxBonus",
			"Kill:MaxPenalty",
			"Kill:RewardBase",
			"Repel:RepelTime",
			"Repel:RepelDistance",
			"Misc:TickerDelay",
			"Flag:FlaggerOnRadar",
			"Flag:FlaggerKillMultiplier",
			"Prize:PrizeFactor",
			"Prize:PrizeDelay",
			"Prize:MinimumVirtual",
			"Prize:UpgradeVirtual",
			"Prize:PrizeMaxExist",
			"Prize:PrizeMinExist",
			"Prize:PrizeNegativeFactor",
			"Door:DoorDelay",
			"Toggle:AntiWarpPixels",
			"Door:DoorMode",
			"Flag:FlagBlankDelay",
			"Flag:NoDataFlagDropDelay",
			"Prize:MultiPrizeCount",
			"Brick:BrickTime",
			"Misc:WarpRadiusLimit",
			"Bomb:EBombShutdownTime",
			"Bomb:EBombDamagePercent",
			"Radar:RadarNeutralSize",
			"Misc:WarpPointDelay",
			"Misc:NearDeathLevel",
			"Bomb:BBombDamagePercent",
			"Shrapnel:ShrapnelDamagePercent",
			"Latency:ClientSlowPacketTime",
			"Flag:FlagDropResetReward",
			"Flag:FlaggerFireCostPercent",
			"Flag:FlaggerDamagePercent",
			"Flag:FlaggerBombFireDelay",
			"Soccer:PassDelay",
			"Soccer:BallBlankDelay",
			"Latency:S2CNoDataKickoutDelay",
			"Flag:FlaggerThrustAdjustment",
			"Flag:FlaggerSpeedAdjustment",
			"Latency:ClientSlowPacketSampleSize",
			"Unused:Unused5",
			"Unused:Unused4",
			"Unused:Unused3",
			"Unused:Unused2",
			"Unused:Unused1"
        };

		[ConfigHelp("Shrapnel", "Random", ConfigScope.Arena, typeof(bool), "Whether shrapnel spreads in circular or random patterns")]
		[ConfigHelp("Soccer", "BallBounce", ConfigScope.Arena, typeof(bool), "Whether the ball bounces off walls")]
		[ConfigHelp("Soccer", "AllowBombs", ConfigScope.Arena, typeof(bool), "Whether the ball carrier can fire his bombs")]
		[ConfigHelp("Soccer", "AllowGuns", ConfigScope.Arena, typeof(bool), "Whether the ball carrier can fire his guns")]
		[ConfigHelp("Soccer", "Mode", ConfigScope.Arena, typeof(Enum), "Goal configuration ($GOAL_ALL, $GOAL_LEFTRIGHT, $GOAL_TOPBOTTOM, $GOAL_CORNERS_3_1, $GOAL_CORNERS_1_3, $GOAL_SIDES_3_1, $GOAL_SIDES_1_3)")]
		//Team:MaxPerTeam
		//Team:MaxPerPrivateTeam
		[ConfigHelp("Mine", "TeamMaxMines", ConfigScope.Arena, typeof(int), "Maximum number of mines allowed to be placed by an entire team")]
		[ConfigHelp("Wormhole", "GravityBombs", ConfigScope.Arena, typeof(bool), "Whether a wormhole affects bombs")]
		[ConfigHelp("Bomb", "BombSafety", ConfigScope.Arena, typeof(bool), "Whether proximity bombs have a firing safety.  If enemy ship is within proximity radius, will it allow you to fire")]
		//Chat:MessageReliable
		[ConfigHelp("Prize", "TakePrizeReliable", ConfigScope.Arena, typeof(int), "Whether prize packets are sent reliably (C2S)")]
		[ConfigHelp("Message", "AllowAudioMessages", ConfigScope.Arena, typeof(bool), "Whether players can send audio messages")]
		[ConfigHelp("Prize", "PrizeHideCount", ConfigScope.Arena, typeof(int), "Number of prizes that are regenerated every PrizeDelay")]
		[ConfigHelp("Misc", "ExtraPositionData", ConfigScope.Arena, typeof(int), "Whether regular players receive sysop data about a ship")]
		[ConfigHelp("Misc", "SlowFrameCheck", ConfigScope.Arena, typeof(int), "Whether to check for slow frames on the client (possible cheat technique) (flawed on some machines, do not use)")]
		[ConfigHelp("Flag", "CarryFlags", ConfigScope.Arena, typeof(int), "Whether the flags can be picked up and carried (0=no, 1=yes, 2=yes-one at a time, 3=yes-two at a time, 4=three, etc..)")]
		[ConfigHelp("Misc", "AllowSavedShips", ConfigScope.Arena, typeof(int), "Whether saved ships are allowed (do not allow saved ship in zones where sub-arenas may have differing parameters)")]
		[ConfigHelp("Radar", "RadarMode", ConfigScope.Arena, typeof(int), "Radar mode (0=normal, 1=half/half, 2=quarters, 3=half/half-see team mates, 4=quarters-see team mates)")]
		[ConfigHelp("Misc", "VictoryMusic", ConfigScope.Arena, typeof(int), "Whether the zone plays victory music or not")]
		[ConfigHelp("Flag", "FlaggerGunUpgrade", ConfigScope.Arena, typeof(bool), "Whether the flaggers get a gun upgrade")]
		[ConfigHelp("Flag", "FlaggerBombUpgrade", ConfigScope.Arena, typeof(bool), "Whether the flaggers get a bomb upgrade")]
		[ConfigHelp("Soccer", "UseFlagger", ConfigScope.Arena, typeof(bool), "If player with soccer ball should use the Flag:Flagger* ship adjustments or not")]
		[ConfigHelp("Soccer", "BallLocation", ConfigScope.Arena, typeof(bool), "Whether the balls location is displayed at all times or not")]
		[ConfigHelp("Misc", "AntiWarpSettleDelay", ConfigScope.Arena, typeof(int), "How many ticks to activate a fake antiwarp after attaching, portaling, or warping.")]
        public static string[] ByteNames = 
        {
			"Shrapnel:Random",
			"Soccer:BallBounce",
			"Soccer:AllowBombs",
			"Soccer:AllowGuns",
			"Soccer:Mode",
			"Team:MaxPerTeam",
			"Team:MaxPerPrivateTeam",
			"Mine:TeamMaxMines",
			"Wormhole:GravityBombs",
			"Bomb:BombSafety",
			"Chat:MessageReliable",
			"Prize:TakePrizeReliable",
			"Message:AllowAudioMessages",
			"Prize:PrizeHideCount",
			"Misc:ExtraPositionData",
			"Misc:SlowFrameCheck",
			"Flag:CarryFlags",
			"Misc:AllowSavedShips",
			"Radar:RadarMode",
			"Misc:VictoryMusic",
			"Flag:FlaggerGunUpgrade",
			"Flag:FlaggerBombUpgrade",
			"Soccer:UseFlagger",
			"Soccer:BallLocation",
			"Misc:AntiWarpSettleDelay",
			"Unused:Unused7",
			"Unused:Unused6",
			"Unused:Unused5",
			"Unused:Unused4",
			"Unused:Unused3",
			"Unused:Unused2",
			"Unused:Unused1"
        };

		[ConfigHelp("PrizeWeight", "QuickCharge", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Recharge' prize appearing")]
		[ConfigHelp("PrizeWeight", "Energy", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Energy Upgrade' prize appearing")]
		[ConfigHelp("PrizeWeight", "Rotation", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Rotation' prize appearing")]
		[ConfigHelp("PrizeWeight", "Stealth", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Stealth' prize appearing")]
		[ConfigHelp("PrizeWeight", "Cloak", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Cloak' prize appearing")]
		[ConfigHelp("PrizeWeight", "XRadar", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'XRadar' prize appearing")]
		[ConfigHelp("PrizeWeight", "Warp", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Warp' prize appearing")]
		[ConfigHelp("PrizeWeight", "Gun", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Gun Upgrade' prize appearing")]
		[ConfigHelp("PrizeWeight", "Bomb", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Bomb Upgrade' prize appearing")]
		[ConfigHelp("PrizeWeight", "BouncingBullets", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Bouncing Bullets' prize appearing")]
		[ConfigHelp("PrizeWeight", "Thruster", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Thruster' prize appearing")]
		[ConfigHelp("PrizeWeight", "TopSpeed", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Speed' prize appearing")]
		[ConfigHelp("PrizeWeight", "Recharge", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Full Charge' prize appearing")]
		[ConfigHelp("PrizeWeight", "Glue", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Engine Shutdown' prize appearing")]
		[ConfigHelp("PrizeWeight", "MultiFire", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'MultiFire' prize appearing")]
		[ConfigHelp("PrizeWeight", "Proximity", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Proximity Bomb' prize appearing")]
		[ConfigHelp("PrizeWeight", "AllWeapons", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Super!' prize appearing")]
		[ConfigHelp("PrizeWeight", "Shields", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Shields' prize appearing")]
		[ConfigHelp("PrizeWeight", "Shrapnel", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Shrapnel Upgrade' prize appearing")]
		[ConfigHelp("PrizeWeight", "AntiWarp", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'AntiWarp' prize appearing")]
		[ConfigHelp("PrizeWeight", "Repel", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Repel' prize appearing")]
		[ConfigHelp("PrizeWeight", "Burst", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Burst' prize appearing")]
		[ConfigHelp("PrizeWeight", "Decoy", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Decoy' prize appearing")]
		[ConfigHelp("PrizeWeight", "Thor", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Thor' prize appearing")]
		[ConfigHelp("PrizeWeight", "MultiPrize", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Multi-Prize' prize appearing")]
		[ConfigHelp("PrizeWeight", "Brick", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Brick' prize appearing")]
		[ConfigHelp("PrizeWeight", "Rocket", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Rocket' prize appearing")]
		[ConfigHelp("PrizeWeight", "Portal", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Portal' prize appearing")]
		public static readonly string[] PrizeWeightNames = 
        {
			"PrizeWeight:QuickCharge",
			"PrizeWeight:Energy",
			"PrizeWeight:Rotation",
			"PrizeWeight:Stealth",
			"PrizeWeight:Cloak",
			"PrizeWeight:XRadar",
			"PrizeWeight:Warp",
			"PrizeWeight:Gun",
			"PrizeWeight:Bomb",
			"PrizeWeight:BouncingBullets",
			"PrizeWeight:Thruster",
			"PrizeWeight:TopSpeed",
			"PrizeWeight:Recharge",
			"PrizeWeight:Glue",
			"PrizeWeight:MultiFire",
			"PrizeWeight:Proximity",
			"PrizeWeight:AllWeapons",
			"PrizeWeight:Shields",
			"PrizeWeight:Shrapnel",
			"PrizeWeight:AntiWarp",
			"PrizeWeight:Repel",
			"PrizeWeight:Burst",
			"PrizeWeight:Decoy",
			"PrizeWeight:Thor",
			"PrizeWeight:MultiPrize",
			"PrizeWeight:Brick",
			"PrizeWeight:Rocket",
			"PrizeWeight:Portal"
        };

		[ConfigHelp("DPrizeWeight", "QuickCharge", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Recharge' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Energy", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Energy Upgrade' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Rotation", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Rotation' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Stealth", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Stealth' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Cloak", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Cloak' prize appearing")]
		[ConfigHelp("DPrizeWeight", "XRadar", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'XRadar' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Warp", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Warp' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Gun", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Gun Upgrade' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Bomb", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Bomb Upgrade' prize appearing")]
		[ConfigHelp("DPrizeWeight", "BouncingBullets", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Bouncing Bullets' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Thruster", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Thruster' prize appearing")]
		[ConfigHelp("DPrizeWeight", "TopSpeed", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Speed' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Recharge", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Full Charge' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Glue", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Engine Shutdown' prize appearing")]
		[ConfigHelp("DPrizeWeight", "MultiFire", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'MultiFire' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Proximity", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Proximity Bomb' prize appearing")]
		[ConfigHelp("DPrizeWeight", "AllWeapons", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Super!' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Shields", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Shields' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Shrapnel", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Shrapnel Upgrade' prize appearing")]
		[ConfigHelp("DPrizeWeight", "AntiWarp", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'AntiWarp' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Repel", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Repel' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Burst", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Burst' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Decoy", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Decoy' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Thor", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Thor' prize appearing")]
		[ConfigHelp("DPrizeWeight", "MultiPrize", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Multi-Prize' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Brick", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Brick' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Rocket", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Rocket' prize appearing")]
		[ConfigHelp("DPrizeWeight", "Portal", ConfigScope.Arena, typeof(byte), DefaultValue = "0", Range = "[0-255]", Description = "Likelihood of 'Portal' prize appearing")]
		public static readonly string[] DeathPrizeWeightNames =
		{
			"DPrizeWeight:QuickCharge",
			"DPrizeWeight:Energy",
			"DPrizeWeight:Rotation",
			"DPrizeWeight:Stealth",
			"DPrizeWeight:Cloak",
			"DPrizeWeight:XRadar",
			"DPrizeWeight:Warp",
			"DPrizeWeight:Gun",
			"DPrizeWeight:Bomb",
			"DPrizeWeight:BouncingBullets",
			"DPrizeWeight:Thruster",
			"DPrizeWeight:TopSpeed",
			"DPrizeWeight:Recharge",
			"DPrizeWeight:Glue",
			"DPrizeWeight:MultiFire",
			"DPrizeWeight:Proximity",
			"DPrizeWeight:AllWeapons",
			"DPrizeWeight:Shields",
			"DPrizeWeight:Shrapnel",
			"DPrizeWeight:AntiWarp",
			"DPrizeWeight:Repel",
			"DPrizeWeight:Burst",
			"DPrizeWeight:Decoy",
			"DPrizeWeight:Thor",
			"DPrizeWeight:MultiPrize",
			"DPrizeWeight:Brick",
			"DPrizeWeight:Rocket",
			"DPrizeWeight:Portal"
		};

        /* the following names are only key names, not key+section names */

		[ConfigHelp("All", "SuperTime", ConfigScope.Arena, typeof(int), "How long Super lasts on the ship (in ticks)")]
		[ConfigHelp("All", "ShieldsTime", ConfigScope.Arena, typeof(int), "How long Shields lasts on the ship (in ticks)")]
        public static readonly string[] ShipLongNames = 
        {
			"SuperTime",
			"ShieldsTime"
        };

		[ConfigHelp("All", "Gravity", ConfigScope.Arena, typeof(int), "How strong of an effect the wormhole has on this ship (0 = none)")]
		[ConfigHelp("All", "GravityTopSpeed", ConfigScope.Arena, typeof(int), "Ship are allowed to move faster than their maximum speed while effected by a wormhole.  This determines how much faster they can go (0 = no extra speed)")]
		[ConfigHelp("All", "BulletFireEnergy", ConfigScope.Arena, typeof(int), "Amount of energy it takes a ship to fire a single L1 bullet")]
		[ConfigHelp("All", "MultiFireEnergy", ConfigScope.Arena, typeof(int), "Amount of energy it takes a ship to fire multifire L1 bullets")]
		[ConfigHelp("All", "BombFireEnergy", ConfigScope.Arena, typeof(int), "Amount of energy it takes a ship to fire a single bomb")]
		[ConfigHelp("All", "BombFireEnergyUpgrade", ConfigScope.Arena, typeof(int), "Extra amount of energy it takes a ship to fire an upgraded bomb. i.e. L2 = BombFireEnergy+BombFireEnergyUpgrade")]
		[ConfigHelp("All", "LandmineFireEnergy", ConfigScope.Arena, typeof(int), "Amount of energy it takes a ship to place a single L1 mine")]
		[ConfigHelp("All", "LandmineFireEnergyUpgrade", ConfigScope.Arena, typeof(int), "Extra amount of energy it takes to place an upgraded landmine. i.e. L2 = LandmineFireEnergy+LandmineFireEnergyUpgrade")]
		[ConfigHelp("All", "BulletSpeed", ConfigScope.Arena, typeof(int), "How fast bullets travel")]
		[ConfigHelp("All", "BombSpeed", ConfigScope.Arena, typeof(int), "How fast bombs travel")]
		[ConfigHelp("All", "SeeBombLevel", ConfigScope.Arena, typeof(int), Range = "[0-4]", Description = "If ship can see bombs on radar (0=Disabled, 1=All, 2=L2 and up, 3=L3 and up, 4=L4 bombs only)")]
		[ConfigHelp("All", "DisableFastShooting", ConfigScope.Arena, typeof(bool), "If firing bullets, bombs, or thors is disabled after using afterburners (1=enabled) (Cont .36+)")]
		[ConfigHelp("All", "Radius", ConfigScope.Arena, typeof(int), Range = "[0-255]", DefaultValue = "14", Description = "The ship's radius from center to outside, in pixels. (Cont .37+)")]
		[ConfigHelp("All", "MultiFireAngle", ConfigScope.Arena, typeof(int), "Angle spread between multi-fire bullets and standard forward firing bullets (111 = 1 degree, 1000 = 1 ship-rotation-point)")]
		[ConfigHelp("All", "CloakEnergy", ConfigScope.Arena, typeof(int), "Amount of energy required to have 'Cloak' activated (thousanths per tick)")]
		[ConfigHelp("All", "StealthEnergy", ConfigScope.Arena, typeof(int), "Amount of energy required to have 'Stealth' activated (thousanths per tick)")]
		[ConfigHelp("All", "AntiWarpEnergy", ConfigScope.Arena, typeof(int), "Amount of energy required to have 'Anti-Warp' activated (thousanths per tick)")]
		[ConfigHelp("All", "XRadarEnergy", ConfigScope.Arena, typeof(int), "Amount of energy required to have 'X-Radar' activated (thousanths per tick)")]
		[ConfigHelp("All", "MaximumRotation", ConfigScope.Arena, typeof(int), "Maximum rotation rate of the ship (0 = can't rotate, 400 = full rotation in 1 second)")]
		[ConfigHelp("All", "MaximumThrust", ConfigScope.Arena, typeof(int), "Maximum thrust of ship (0 = none)")]
		[ConfigHelp("All", "MaximumSpeed", ConfigScope.Arena, typeof(int), "Maximum speed of ship (0 = can't move)")]
		[ConfigHelp("All", "MaximumRecharge", ConfigScope.Arena, typeof(int), "Maximum recharge rate, or how quickly this ship recharges its energy")]
		[ConfigHelp("All", "MaximumEnergy", ConfigScope.Arena, typeof(int), "Maximum amount of energy that the ship can have")]
		[ConfigHelp("All", "InitialRotation", ConfigScope.Arena, typeof(int), "Initial rotation rate of the ship (0 = can't rotate, 400 = full rotation in 1 second)")]
		[ConfigHelp("All", "InitialThrust", ConfigScope.Arena, typeof(int), "Initial thrust of ship (0 = none)")]
		[ConfigHelp("All", "InitialSpeed", ConfigScope.Arena, typeof(int), "Initial speed of ship (0 = can't move)")]
		[ConfigHelp("All", "InitialRecharge", ConfigScope.Arena, typeof(int), "Initial recharge rate, or how quickly this ship recharges its energy")]
		[ConfigHelp("All", "InitialEnergy", ConfigScope.Arena, typeof(int), "Initial amount of energy that the ship can have")]
		[ConfigHelp("All", "UpgradeRotation", ConfigScope.Arena, typeof(int), "Amount added per 'Rotation' Prize")]
		[ConfigHelp("All", "UpgradeThrust", ConfigScope.Arena, typeof(int), "Amount added per 'Thruster' Prize")]
		[ConfigHelp("All", "UpgradeSpeed", ConfigScope.Arena, typeof(int), "Amount added per 'Speed' Prize")]
		[ConfigHelp("All", "UpgradeRecharge", ConfigScope.Arena, typeof(int), "Amount added per 'Recharge Rate' Prize")]
		[ConfigHelp("All", "UpgradeEnergy", ConfigScope.Arena, typeof(int), "Amount added per 'Energy Upgrade' Prize")]
		[ConfigHelp("All", "AfterburnerEnergy", ConfigScope.Arena, typeof(int), "Amount of energy required to have 'Afterburners' activated")]
		[ConfigHelp("All", "BombThrust", ConfigScope.Arena, typeof(int), "Amount of back-thrust you receive when firing a bomb")]
		[ConfigHelp("All", "BurstSpeed", ConfigScope.Arena, typeof(int), "How fast the burst shrapnel is for this ship")]
		[ConfigHelp("All", "TurretThrustPenalty", ConfigScope.Arena, typeof(int), "Amount the ship's thrust is decreased with a turret riding")]
		[ConfigHelp("All", "TurretSpeedPenalty", ConfigScope.Arena, typeof(int), "Amount the ship's speed is decreased with a turret riding")]
		[ConfigHelp("All", "BulletFireDelay", ConfigScope.Arena, typeof(int), "Delay that ship waits after a bullet is fired until another weapon may be fired (in ticks)")]
		[ConfigHelp("All", "MultiFireDelay", ConfigScope.Arena, typeof(int), "Delay that ship waits after a multifire bullet is fired until another weapon may be fired (in ticks)")]
		[ConfigHelp("All", "BombFireDelay", ConfigScope.Arena, typeof(int), "Delay that ship waits after a bomb is fired until another weaponmay be fired (in ticks)")]
		[ConfigHelp("All", "LandmineFireDelay", ConfigScope.Arena, typeof(int), "Delay that ship waits after a mine is fired until another weapon may be fired (in ticks)")]
		[ConfigHelp("All", "RocketTime", ConfigScope.Arena, typeof(int), "How long a Rocket lasts (in ticks)")]
		[ConfigHelp("All", "InitialBounty", ConfigScope.Arena, typeof(int), "Number of 'Greens' given to ships when they start")]
		[ConfigHelp("All", "DamageFactor", ConfigScope.Arena, typeof(int), "How likely a the ship is to take damamage (ie. lose a prize) (0=special-case-never, 1=extremely likely, 5000=almost never)")]
		[ConfigHelp("All", "PrizeShareLimit", ConfigScope.Arena, typeof(int), "Maximum bounty that ships receive Team Prizes")]
		[ConfigHelp("All", "AttachBounty", ConfigScope.Arena, typeof(int), "Bounty required by ships to attach as a turret")]
		[ConfigHelp("All", "SoccerThrowTime", ConfigScope.Arena, typeof(int), "Time player has to carry soccer ball (in ticks)")]
		[ConfigHelp("All", "SoccerBallFriction", ConfigScope.Arena, typeof(int), "Amount the friction on the soccer ball (how quickly it slows down -- higher numbers mean faster slowdown)")]
		[ConfigHelp("All", "SoccerBallProximity", ConfigScope.Arena, typeof(int), "How close the player must be in order to pick up ball (in pixels)")]
		[ConfigHelp("All", "SoccerBallSpeed", ConfigScope.Arena, typeof(int), "Initial speed given to the ball when fired by the carrier")]
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

		[ConfigHelp("All", "TurretLimit", ConfigScope.Arena, typeof(int), "Number of turrets allowed on a ship")]
		[ConfigHelp("All", "BurstShrapnel", ConfigScope.Arena, typeof(int), "Number of bullets released when a 'Burst' is activated")]
		[ConfigHelp("All", "MaxMines", ConfigScope.Arena, typeof(int), "Maximum number of mines allowed in ships")]
		[ConfigHelp("All", "RepelMax", ConfigScope.Arena, typeof(int), "Maximum number of Repels allowed in ships")]
		[ConfigHelp("All", "BurstMax", ConfigScope.Arena, typeof(int), "Maximum number of Bursts allowed in ships")]
		[ConfigHelp("All", "DecoyMax", ConfigScope.Arena, typeof(int), "Maximum number of Decoys allowed in ships")]
		[ConfigHelp("All", "ThorMax", ConfigScope.Arena, typeof(int), "Maximum number of Thor's Hammers allowed in ships")]
		[ConfigHelp("All", "BrickMax", ConfigScope.Arena, typeof(int), "Maximum number of Bricks allowed in ships")]
		[ConfigHelp("All", "RocketMax", ConfigScope.Arena, typeof(int), "Maximum number of Rockets allowed in ships")]
		[ConfigHelp("All", "PortalMax", ConfigScope.Arena, typeof(int), "Maximum number of Portals allowed in ships")]
		[ConfigHelp("All", "InitialRepel", ConfigScope.Arena, typeof(int), "Initial number of Repels given to ships when they start")]
		[ConfigHelp("All", "InitialBurst", ConfigScope.Arena, typeof(int), "Initial number of Bursts given to ships when they start")]
		[ConfigHelp("All", "InitialBrick", ConfigScope.Arena, typeof(int), "Initial number of Bricks given to ships when they start")]
		[ConfigHelp("All", "InitialRocket", ConfigScope.Arena, typeof(int), "Initial number of Rockets given to ships when they start")]
		[ConfigHelp("All", "InitialThor", ConfigScope.Arena, typeof(int), "Initial number of Thor's Hammers given to ships when they start")]
		[ConfigHelp("All", "InitialDecoy", ConfigScope.Arena, typeof(int), "Initial number of Decoys given to ships when they start")]
		[ConfigHelp("All", "InitialPortal", ConfigScope.Arena, typeof(int), "Initial number of Portals given to ships when they start")]
		[ConfigHelp("All", "BombBounceCount", ConfigScope.Arena, typeof(int), "Number of times a ship's bombs bounce before they explode on impact")]
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

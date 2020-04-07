using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core
{
    /// <summary>
    /// equivalent of param.h
    /// </summary>
    public static class Constants
    {
        /// <summary>
        /// the search paths for config files (colon delimited with placeholders)
        /// </summary>
        public const string CFG_CONFIG_SEARCH_PATH = "arenas/%b/%n:conf/%n:%n:arenas/(default)/%n";

        /// <summary>
        /// the search paths for lvl files (colon delimited with placeholders)
        /// %b = base arena name (no trailing number)
        /// %m = map file name
        /// </summary>
        public const string CFG_LVL_SEARCH_PATH = "arenas/%b/%m:maps/%m:%m:arenas/%b/%b.lvl:maps/%b.lvl:arenas/(default)/%m";

        /// <summary>
        /// the search paths for lvz files (colon delimited with placeholders)
        /// %b = base arena name (no trailing number)
        /// %m = lvz file name
        /// </summary>
        public const string CFG_LVZ_SEARCH_PATH = "arenas/%b/%m:maps/%m:%m:arenas/(default)/%m";

        /// <summary>
        /// how many incoming rel packets to buffer for a client
        /// </summary>
        public const int CFG_INCOMING_BUFFER = 32;

        public const int MaxPacket = 512;

        public const int MaxLvzFiles = 16;

        /// <summary>
        /// maximum size of a "big packet" allowed to recieve
        /// </summary>
        public const int CFG_MAX_BIG_PACKET = 65536;
        public const int MaxBigPacket = CFG_MAX_BIG_PACKET;

        /// <summary>
        /// how many bytes to 'chunk' data into when sending "big packets"
        /// (this includes sized send data (eg, map/news/lvz downloads))
        /// </summary>
        public const int ChunkSize = 480;

        public const int ReliableHeaderLen = 6;

        public const int RandMax = 0x7fff;

        public const string AG_PUBLIC = "(public)";
        public const string AG_GLOBAL = "(global)";

        /// <summary>
        /// callbacks / events
        /// </summary>
        public static class Events
        {
            public const string ConnectionInit = "conninit";
            public const string PlayerAction = "playeraction";
            public const string ArenaAction = "ArenaAction";
            public const string ChatMessage = "chatmessage";
            public const string SafeZone = "safezone";
            public const string FreqChange = "freqchange";
            public const string ShipChange = "shipchange";
            public const string Green = "green";
            public const string Log = "log";
            public const string Kill = "kill";
            public const string PostKill = "postkill";
            public const string Attach = "attach";
            public const string MapRegion = "mapregion";

            /// <summary>
            /// this callback is called whenever a Player is allocated or
            /// deallocated. in general you probably want to use CB_PLAYERACTION
            /// instead of this callback for general initialization tasks.
            /// </summary>
            /// <remarks>NewPlayerDelegate</remarks>
            public const string NewPlayer = "newplayer";
        }

        /// <summary>
        /// some standard capability names
        /// </summary>
        public static class Capabilities
        {
            /// <summary>
            /// if a player can see mod chat messages
            /// </summary>
            public const string ModChat = "seemodchat";

            /// <summary>
            /// if a player can send mod chat messages
            /// </summary>
            public const string SendModChat = "sendmodchat";

            /// <summary>
            /// if a player can send voice messages
            /// </summary>
            public const string SoundMessages = "sendsoundmessages";

            /// <summary>
            /// if a player can upload files (note that this is separate from cmd_putfile, and both are required to use ?putfile)
            /// </summary>
            public const string UploadFile = "uploadfile";

            /// <summary>
            /// if a player can see urgent log messages from all arenas
            /// </summary>
            public const string SeeSysopLogAll = "seesysoplogall";

            /// <summary>
            /// if a player can see urgent log messages from the arena he's in
            /// </summary>
            public const string SeeSysopLogArena = "seesysoplogarena";

            /// <summary>
            /// if a player can see private arenas (in ?arena, ?listmod, etc.)
            /// </summary>
            public const string SeePrivArena = "seeprivarena";

            /// <summary>
            /// if a player can see private freqs
            /// </summary>
            public const string SeePrivFreq = "seeprivfreq";

            /// <summary>
            /// if a player can stay connected despite security checksum failures
            /// </summary>
            public const string BypassSecurity = "bypasssecurity";

            /// <summary>
            /// if a client can send object change broadcast packets (as some bots might want to do)
            /// </summary>
            public const string BroadcastBot = "broadcastbot";

            /// <summary>
            /// if a client can send arbitrary broadcast packets (shouldn't ever give this out)
            /// </summary>
            public const string BroadcastAny = "broadcastany";

            /// <summary>
            /// if a player can avoid showing up in ?spec output
            /// </summary>
            public const string InvisibleSpectator = "invisiblespectator";

            /// <summary>
            /// if a client can escape chat flood detection
            /// </summary>
            public const string CanSpam = "unlimitedchat";

            /// <summary>
            /// if a client can use the settings change packet (note this is separate from cmd_quickfix/cmd_getsettings and both are required to use ?quickfix/?getsettings)
            /// </summary>
            public const string ChangeSettings = "changesettings";

            /// <summary>
            /// if a player shows up in ?listmod output
            /// </summary>
            public const string IsStaff = "isstaff";

            /// <summary>
            /// if a player can sees all non-group-default players even if they lack isstaff
            /// </summary>
            public const string SeeAllStaff = "seeallstaff";

            /// <summary>
            /// if a player can change ships even if locked
            /// </summary>
            public const string BypassLock = "bypasslock";

            /// <summary>
            /// if a player can see the energy of other players
            /// </summary>
            public const string SeeEnergy = "seenrg";

            /// <summary>
            /// if a player can see extra player data of other players
            /// </summary>
            public const string SeeExtraPlayerData = "seeepd";
        }
    }
}

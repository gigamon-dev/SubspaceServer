using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SS.Core
{
    /// <summary>
    /// Constants.
    /// </summary>
    /// <remarks>
    /// This is the equivalent of param.h in ASSS.
    /// </remarks>
    public static class Constants
    {
        #region Search Paths

        /// <summary>
        /// Search paths for config files with placeholders:
        /// <list type="table">
        /// <item><term>{0}</term><description>file name<para>Can be just a file name. (e.g. "arena.conf")</para><para>Or a file name with path (e.g. "conf/svs/svs.conf")</para></description></item>
        /// <item><term>{1}</term><description>arena name</description></item>
        /// </list>
        /// </summary>
        public static readonly IReadOnlyCollection<string> ConfigSearchPaths = new ReadOnlyCollection<string>(
            new string[] {
                "arenas/{1}/{0}",
                "conf/{0}",
                "{0}",
                "arenas/(default)/{0}",
            });

        /// <summary>
        /// Search paths for lvl files with placeholders:
        /// <list type="table">
        /// <item><term>{0}</term><description>map file name</description></item>
        /// <item><term>{1}</term><description>base arena name (no trailing number)</description></item>
        /// </list>
        /// </summary>
        public static readonly IReadOnlyCollection<string> LvlSearchPaths = new ReadOnlyCollection<string>(
            new string[] {
                "arenas/{1}/{0}",
                "maps/{0}",
                "{0}",
                "arenas/{1}/{1}.lvl",
                "maps/{1}.lvl",
                "arenas/(default)/{0}",
            });

        /// <summary>
        /// Search paths for lvz files with placeholders:
        /// <list type="table">
        /// <item><term>{0}</term><description>map file name</description></item>
        /// <item><term>{1}</term><description>base arena name (no trailing number)</description></item>
        /// </list>
        /// </summary>
        public static readonly IReadOnlyCollection<string> LvzSearchPaths = new ReadOnlyCollection<string>(
            new string[] {
                "arenas/{1}/{0}",
                ":maps/{0}",
                ":{0}",
                ":arenas/(default)/{0}",
            });

        #endregion

        #region Network related

        /// <summary>
        /// The maximum number of incoming reliable packets to buffer for a player connection.
        /// </summary>
        public const int IncomingReliableBufferLength_Player = 32;

        /// <summary>
        /// The maximum number of incoming reliable packets to buffer for a client connection.
        /// </summary>
        public const int IncomingReliableBufferLength_ClientConnection = 32; // TODO: Maybe use a larger buffer for client connections? e.g., to allow the connection to the biller to receive a large amount of data, such as chat

        /// <summary>
        /// Maximum # of bytes a game packet can be.
        /// </summary>
        public const int MaxPacket = 512; // TODO: maybe this can be increased to 520?

        /// <summary>
        /// Maximum # of bytes a grouped packet (00 0E) can be.
        /// </summary>
        public const int MaxGroupedPacketLength = 512;

        /// <summary>
        /// Maximum # of bytes an item within a grouped packet (00 0E) can be.
        /// </summary>
        public const int MaxGroupedPacketItemLength = byte.MaxValue; // Grouped packets use a single byte for the item length.

        /// <summary>
        /// Maximum size for receiving packets (due to the size of a "Connection Init" packets).
        /// </summary>
        public const int MaxConnInitPacket = 2048;

        /// <summary>
        /// Maximum size of a "big packet" in bytes.
        /// </summary>
        public const int MaxBigPacket = 65536;

        /// <summary>
        /// how many bytes to 'chunk' data into when sending "big packets"
        /// (this includes sized send data (eg, map/news/lvz downloads))
        /// </summary>
        public const int ChunkSize = 480;

		#endregion

		#region Target Item Counts

		/// <summary>
		/// The expected upper limit on the # of players.
		/// </summary>
		/// <remarks>
		/// This is used to initialize collections with enough starting capacity such that they will likely never need to be resized.
		/// </remarks>
		public const int TargetPlayerCount = 512;

		/// <summary>
		/// The expected upper limit on the # of arenas.
		/// </summary>
		/// <remarks>
		/// This is used to initialize collections with enough starting capacity such that they will likely never need to be resized.
		/// </remarks>
		public const int TargetArenaCount = 64;

		/// <summary>
		/// The expected upper limit on the # of per-player data registrations.
		/// </summary>
		/// <remarks>
		/// This is used to initialize collections with enough starting capacity such that they will likely never need to be resized.
		/// </remarks>
		public const int TargetPlayerExtraDataCount = 64;

		/// <summary>
		/// The expected upper limit on the # of per-arena data registrations.
		/// </summary>
		/// <remarks>
		/// This is used to initialize collections with enough starting capacity such that they will likely never need to be resized.
		/// </remarks>
		public const int TargetArenaExtraDataCount = 64;

        #endregion

        public const int RandMax = 0x7fff;

        /// <summary>
        /// The maximum length of an arena name. Since Subspace uses a single-byte character encoding, this maximum is both the # of bytes and # of characters.
        /// </summary>
        /// <remarks>
        /// The arena name field of the C2S 0x01 (Go Arena) packet is 16 bytes, with the last byte required to be a null-terminator.
        /// </remarks>
        public const int MaxArenaNameLength = 15;

        /// <summary>
        /// The maximum length of a player name. Since Subspace uses a single-byte character encoding, this maximum is both the # of bytes and # of characters.
        /// </summary>
        /// <remarks>
        /// The Subspace game protocol's S2C 0x03 (Player Entering) packet has player name as 20 bytes (not necessarily null-terminated).
        /// <para>
        /// The UDP billing protocol's 0x01 (User Login) packet has a field size of 24 bytes.
        /// In ASSS the Player struct's "name" field is also 24 bytes.
        /// However, looking VERY closely at the ASSS logic reveals it will store up to 20 characters + 1 byte for the null-terminator.
        /// <code>astrncpy(p->name, auth->name, 21);</code>
        /// In other words, ASSS truncates to 20 characters.
        /// </para>
        /// </remarks>
        public const int MaxPlayerNameLength = 20;

        /// <summary>
        /// The maximum length of a squad name. Since Subspace uses a single-byte character encoding, this maximum is both the # of bytes and # of characters.
        /// </summary>
        /// <remarks>
        /// The Subspace game protocol's S2C 0x03 (Player Entering) packet has squad name as 20 bytes (not necessarily null-terminated).
        /// <para>
        /// The UDP billing protocol's 0x01 (User Login) packet, which has a field size of 24 bytes.
        /// In ASSS the Player struct's "squad" field is also 24 bytes.
        /// However, looking VERY closely at the ASSS logic reveals it will store up to 20 characters + 1 byte for the null-terminator.
        /// <code>astrncpy(p->squad, auth->squad, 21);</code>
        /// In other words, ASSS truncates to 20 characters.
        /// </para>
        /// </remarks>
        public const int MaxSquadNameLength = 20;

        /// <summary>
        /// Represents all public arenas.
        /// </summary>
        /// <remarks>
        /// Public arenas use this as their <see cref="Arena.BaseName"/>.
        /// </remarks>
        public const string ArenaGroup_Public = "(public)";

        /// <summary>
        /// Represents all arenas.
        /// </summary>
        public const string ArenaGroup_Global = "(global)";

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
            /// if a security warnings are suppressed for the player
            /// </summary>
            public const string SuppressSecurity = "suppresssecurity";

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
            /// if a player can see all non-group-default players even if they lack isstaff
            /// </summary>
            public const string SeeAllStaff = "seeallstaff";

            /// <summary>
            /// if a player always forces a change with setship or setfreq instead of going by the arena freqman
            /// </summary>
            public const string ForceShipFreqChange = "forceshipfreqchange";

            /// <summary>
            /// if a player is excluded from the population count (useful for bots)
            /// </summary>
            public const string ExcludePopulation = "excludepopulation";

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

            /// <summary>
            /// If a player can set their banner.
            /// </summary>
            public const string SetBanner = "setbanner";
        }
    }
}

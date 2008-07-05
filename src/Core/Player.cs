using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using SS.Core.Packets;
using System.Collections.Specialized;
using SS.Utilities;

namespace SS.Core
{
    /// <summary>
    /// playeraction event codes
    /// </summary>
    public enum PlayerAction
    {
        /// <summary>
        /// the player is connecting to the server. not arena-specific
        /// </summary>
        Connect,

        /// <summary>
        /// the player is disconnecting from the server. not arena-specific.
        /// </summary>
        Disconnect,

        /// <summary>
        /// this is called at the earliest point after a player indicates an
        /// intention to enter an arena.
        /// you can use this for some questionable stuff, like redirecting
        /// the player to a different arena. but in general it's better to
        /// use EnterArena for general stuff that should happen on
        /// entering arenas.
        /// </summary>
        PreEnterArena,

        /// <summary>
        /// the player is entering an arena.
        /// </summary>
        EnterArena,

        /// <summary>
        /// the player is leaving an arena.
        /// </summary>
        LeaveArena,

        /// <summary>
        /// this is called at some point after the player has sent his first
        /// position packet (indicating that he's joined the game, as
        /// opposed to still downloading a map).
        /// </summary>
        EnterGame,
    }

    public enum ClientType
    {
        /// <summary>
        /// Client type is unknown
        /// </summary>
        Unknown, 

        /// <summary>
        /// Fake client (autoturret, etc)
        /// </summary>
        Fake, 

        /// <summary>
        /// SubSpace client
        /// </summary>
        VIE, 

        /// <summary>
        /// Continuum client
        /// </summary>
        Continuum, 

        /// <summary>
        /// ASSS ChatNet client
        /// </summary>
        Chat
    }

    /// <summary>
    /// player status codes
    /// </summary>
    public enum PlayerState
    {
        /// <summary>
        /// player was just created, and isn't ready to do anything yet
        /// transitions to: connected
        /// </summary>
        Uninitialized = 0, 

        /// <summary>
        /// player is connected (key exchange completed) but has not logged in yet
        /// transitions to: need_auth or leaving_zone
        /// </summary>
        Connected, 

        /// <summary>
        /// player sent login, auth request will be sent
        /// transitions to: wait_auth
        /// </summary>
        NeedAuth, 

        /// <summary>
        /// waiting for auth response
        /// transitions to: connected or need_global_sync
        /// </summary>
        WaitAuth, 

        /// <summary>
        /// auth done, will request global sync
        /// transitions to: wait_global_sync1
        /// </summary>
        NeedGlobalSync, 

        /// <summary>
        /// waiting for sync global persistent data to complete
        /// transitions to: do_global_callbacks
        /// </summary>
        WaitGlobalSync1, 

        /// <summary>
        /// global sync done, will call global player connecting callbacks
        /// transitions to: send_login_response
        /// </summary>
        DoGlobalCallbacks, 

        /// <summary>
        /// callbacks done, will send arena response
        /// transitions to: loggedin
        /// </summary>
        SendLoginResponse, 

        /// <summary>
        /// player is finished logging in but is not in an arena yet status
        /// returns here after leaving an arena, also
        /// transitions to: do_freq_and_arena_sync or leaving_zone
        /// </summary>
        LoggedIn, 

        // p->arena is valid starting here

        /// <summary>
        /// player has requested entering an arena, needs to be assigned a
        /// freq and have arena data syched
        /// transitions to: wait_arena_sync1 (or loggedin)
        /// </summary>
        DoFreqAndArenaSync, 

        /// <summary>
        /// waiting for scores sync
        /// transitions to: send_arena_response (or do_arena_sync2)
        /// </summary>
        WaitArenaSync1, 

        /// <summary>
        /// done with scores, needs to send arena response and run arena
        /// entering callbacks
        /// transitions to: playing (or do_arena_sync2)
        /// </summary>
        ArenaRespAndCBS, 

        /** player is playing in an arena. typically the longest state */
        /* transitions to: leaving_arena */
        Playing, 

        /// <summary>
        /// player has left arena, callbacks need to be called
        /// transitions to: do_arena_sync2
        /// </summary>
        LeavingArena, 

        /// <summary>
        /// need to sync in the other direction
        /// transitions to: wait_arena_sync2
        /// </summary>
        DoArenaSync2, 

        /// <summary>
        /// waiting for scores sync, other direction
        /// transitions to: loggedin
        /// </summary>
        WaitArenaSync2, 

        // p->arena is no longer valid after this point

        /// <summary>
        /// player is leaving zone, call disconnecting callbacks and start
        /// global sync
        /// transitions to: wait_global_sync2
        /// </summary>
        LeavingZone, 

        /// <summary>
        /// waiting for global sync, other direction
        /// transitions to: timewait
        /// </summary>
        WaitGlobalSync2, 

        /// <summary>
        /// the connection is all set to be ended. the network layer will
        /// free the player after this.
        /// transitions to: (none)
        /// </summary>
        TimeWait
    };

    /// <summary>
    /// this encapsulates a bunch of the typical position information about players in standard clients.
    /// </summary>
    public struct PlayerPosition
    {
        public int x;           /**< x coordinate of current position in pixels */
        public int y;           /**< y coordinate of current position in pixels */
        public int xspeed;      /**< velocity in positive x direction (pixels/second) */
        public int yspeed;      /**< velocity in positive y direction (pixels/second) */
        public int rotation;    /**< rotation value (0-63) */
        public uint bounty;     /**< current bounty */
        public PlayerPositionStatus status;     /**< status bitfield */
    };

    public class Player
    {
        public PlayerDataPacket pkt = new PlayerDataPacket(new byte[PlayerDataPacket.Length]);

        public ShipType Ship
        {
            get { return (ShipType)pkt.Ship; }
            set { pkt.Ship = (sbyte)value; }
        }

        public short Freq
        {
            get { return pkt.Freq; }
            set { pkt.Freq = value; }
        }

        /// <summary>
        /// id of the player attached to (-1 means not attached)
        /// </summary>
        public short Attached
        {
            get { return pkt.AttachedTo; }
            set { pkt.AttachedTo = (short)value; }
        }

        /// <summary>
        /// The player ID
        /// </summary>
        public readonly int Id;

        /// <summary>
        /// The client type
        /// </summary>
        public ClientType Type = ClientType.Unknown;

        /// <summary>
        /// The state code
        /// </summary>
        public PlayerState Status = PlayerState.Uninitialized;

        /// <summary>
        /// which state to move to after returning to S_LOGGEDIN
        /// </summary>
        public PlayerState WhenLoggedIn = PlayerState.Uninitialized;

        /// <summary>
        /// the player's current arena, or NULL if not in an arena yet
        /// </summary>
        public Arena Arena;

        /// <summary>
        /// the arena the player is trying to enter
        /// </summary>
        public Arena NewArena;

        /// <summary>
        /// the player's name
        /// </summary>
        public string Name;

        /// <summary>
        /// the player's squad
        /// </summary>
        public string Squad;

        /// <summary>
        /// X screen resolution, for standard clients
        /// </summary>
        public short Xres;

        /// <summary>
        /// Y screen resolution, for standard clients
        /// </summary>
        public short Yres;

        /// <summary>
        /// the time that this player first connected
        /// </summary>
        public DateTime ConnectTime;

        /// <summary>
        /// contains some recent information about the player's position
        /// </summary>
        public PlayerPosition Position;

        /// <summary>
        /// the player's machine id, for standard clients
        /// </summary>
        public uint MacId;
        public uint PermId;

        /// <summary>
        /// ip address the player is connecting from
        /// </summary>
        public IPAddress IpAddress;

        /// <summary>
        /// if the player has connected through a port that sets a default arena, that will be stored here
        /// </summary>
        public string ConnectAs;

        /// <summary>
        /// a text representation of the client connecting
        /// </summary>
        public string ClientName;

        public class PlayerFlags
        {
            private BitVector32 flagVector = new BitVector32(0);

            /// <summary>
            /// if the player has been authenticated by either a billing server or a password file
            /// </summary>
            public bool Authenticated
            {
                get { return flagVector[BitVector32Masks.GetMask(0)]; }
                set { flagVector[BitVector32Masks.GetMask(0)] = value; }
            }

            /// <summary>
            /// set when the player has changed freqs or ships, but before he has acknowleged it
            /// </summary>
            public bool DuringChange
            {
                get { return flagVector[BitVector32Masks.GetMask(1)]; }
                set { flagVector[BitVector32Masks.GetMask(1)] = value; }
            }

            /// <summary>
            /// if player wants optional .lvz files
            /// </summary>
            public bool WantAllLvz
            {
                get { return flagVector[BitVector32Masks.GetMask(2)]; }
                set { flagVector[BitVector32Masks.GetMask(2)] = value; }
            }

            /// <summary>
            /// if player is waiting for db query results
            /// </summary>
            public bool DuringQuery
            {
                get { return flagVector[BitVector32Masks.GetMask(3)]; }
                set { flagVector[BitVector32Masks.GetMask(3)] = value; }
            }

            /// <summary>
            /// if the player's lag is too high to let him be in a ship
            /// </summary>
            public bool NoShip
            {
                get { return flagVector[BitVector32Masks.GetMask(4)]; }
                set { flagVector[BitVector32Masks.GetMask(4)] = value; }
            }

            /// <summary>
            /// if the player's lag is too high to let him have flags or balls
            /// </summary>
            public bool NoFlagsBalls
            {
                get { return flagVector[BitVector32Masks.GetMask(5)]; }
                set { flagVector[BitVector32Masks.GetMask(5)] = value; }
            }

            /// <summary>
            /// if the player has sent a position packet since entering the arena
            /// </summary>
            public bool SentPositionPacket
            {
                get { return flagVector[BitVector32Masks.GetMask(6)]; }
                set { flagVector[BitVector32Masks.GetMask(6)] = value; }
            }

            /// <summary>
            /// if the player has sent a position packet with a weapon since this flag was reset
            /// </summary>
            public bool SentWeaponPacket
            {
                get { return flagVector[BitVector32Masks.GetMask(7)]; }
                set { flagVector[BitVector32Masks.GetMask(7)] = value; }
            }

            /// <summary>
            /// if the player is a bot who wants all position packets
            /// </summary>
            public bool SeeAllPositionPackets
            {
                get { return flagVector[BitVector32Masks.GetMask(8)]; }
                set { flagVector[BitVector32Masks.GetMask(8)] = value; }
            }

            /// <summary>
            /// if the player is a bot who wants his own position packets
            /// </summary>
            public bool SeeOwnPosition
            {
                get { return flagVector[BitVector32Masks.GetMask(9)]; }
                set { flagVector[BitVector32Masks.GetMask(9)] = value; }
            }

            /// <summary>
            /// if the player needs to transition to a leaving arena state while wainting for the database to return
            /// </summary>
            public bool LeaveArenaWhenDoneWaiting
            {
                get { return flagVector[BitVector32Masks.GetMask(10)]; }
                set { flagVector[BitVector32Masks.GetMask(10)] = value; }
            }

            /// <summary>
            /// if the player's obscenity filter is on
            /// </summary>
            public bool ObscenityFilter
            {
                get { return flagVector[BitVector32Masks.GetMask(11)]; }
                set { flagVector[BitVector32Masks.GetMask(11)] = value; }
            }
        }

        /// <summary>
        /// some extra flags that don't have a better place to go
        /// </summary>
        public readonly PlayerFlags Flags = new PlayerFlags();

        // used for PPD (Per Player Data)
        // TODO: consider storing this in central lookup instead of a dictionary on each player object
        private Dictionary<int, object> _playerExtraData = new Dictionary<int,object>();

        public Player(int id)
        {
            Id = id;

            // maybe change pid to short?  asss uses int all over, wonder why...
            pkt.Pid = (short)id;
        }

        /// <summary>
        /// Per Player Data
        /// </summary>
        /// <param name="key">key from IPlayerData.AllocatePlayerData()</param>
        /// <returns>the per player data</returns>
        public object this[int key]
        {
            get { return _playerExtraData[key]; }
            set { _playerExtraData[key] = value; }
        }

        /// <summary>
        /// checks if the player type is VIE Client or Continuum
        /// </summary>
        public bool IsStandard
        {
            get { return (Type == ClientType.VIE) || (Type == ClientType.Continuum); }
        }

        public bool IsChat
        {
            get { return Type == ClientType.Chat; }
        }

        public bool IsHuman
        {
            get { return IsStandard || IsChat; }
        }

        internal void RemovePerPlayerData(int key)
        {
            _playerExtraData.Remove(key);
        }

        internal void RemoveAllPerPlayerData()
        {
            _playerExtraData.Clear();
        }
    }
}

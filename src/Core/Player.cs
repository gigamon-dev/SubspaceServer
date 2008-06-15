using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using SS.Core.Packets;
using System.Collections.Specialized;

namespace SS.Core
{
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
        public uint status;     /**< status bitfield */
    };

    [FlagsAttribute]
    public enum PlayerPositionStatus
    {
        /// <summary>
        /// whether stealth is on
        /// </summary>
        Stealth = 1, 

        /// <summary>
        /// whether cloak is on
        /// </summary>
        Cloak = 2, 
        
        /// <summary>
        /// whether xradar is on
        /// </summary>
        XRadar = 4, 
        
        /// <summary>
        /// whether antiwarp is on
        /// </summary>
        Antiwarp = 8, 
        
        /// <summary>
        /// whether to display the flashing image for a few frames
        /// </summary>
        Flash = 16, 

        /// <summary>
        /// whether the player is in a safezone
        /// </summary>
        Safezone = 32, 

        /// <summary>
        /// whether the player is a ufo
        /// </summary>
        Ufo = 64
    }

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

        public int Attached
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
            private BitVector32 flagVector = new BitVector32();

            /// <summary>
            /// if the player has been authenticated by either a billing server or a password file
            /// </summary>
            public bool Authenticated
            {
                get { return flagVector[0]; }
                set { flagVector[0] = value; }
            }

            /// <summary>
            /// set when the player has changed freqs or ships, but before he has acknowleged it
            /// </summary>
            public bool DuringChange
            {
                get { return flagVector[1]; }
                set { flagVector[1] = value; }
            }

            /// <summary>
            /// if player wants optional .lvz files
            /// </summary>
            public bool WantAllLvz
            {
                get { return flagVector[2]; }
                set { flagVector[2] = value; }
            }

            /// <summary>
            /// if player is waiting for db query results
            /// </summary>
            public bool DuringQuery
            {
                get { return flagVector[3]; }
                set { flagVector[3] = value; }
            }

            /// <summary>
            /// if the player's lag is too high to let him be in a ship
            /// </summary>
            public bool NoShip
            {
                get { return flagVector[4]; }
                set { flagVector[4] = value; }
            }

            /// <summary>
            /// if the player's lag is too high to let him have flags or balls
            /// </summary>
            public bool NoFlagsBalls
            {
                get { return flagVector[5]; }
                set { flagVector[5] = value; }
            }

            /// <summary>
            /// if the player has sent a position packet since entering the arena
            /// </summary>
            public bool SentPositionPacket
            {
                get { return flagVector[6]; }
                set { flagVector[6] = value; }
            }

            /// <summary>
            /// if the player has sent a position packet with a weapon since this flag was reset
            /// </summary>
            public bool SentWeaponPacket
            {
                get { return flagVector[7]; }
                set { flagVector[7] = value; }
            }

            /// <summary>
            /// if the player is a bot who wants all position packets
            /// </summary>
            public bool SeeAllPositionPackets
            {
                get { return flagVector[8]; }
                set { flagVector[8] = value; }
            }

            /// <summary>
            /// if the player is a bot who wants his own position packets
            /// </summary>
            public bool SeeOwnPosition
            {
                get { return flagVector[9]; }
                set { flagVector[9] = value; }
            }

            /// <summary>
            /// if the player needs to transition to a leaving arena state while wainting for the database to return
            /// </summary>
            public bool LeaveArenaWhenDoneWaiting
            {
                get { return flagVector[10]; }
                set { flagVector[10] = value; }
            }

            /// <summary>
            /// if the player's obscenity filter is on
            /// </summary>
            public bool ObscenityFilter
            {
                get { return flagVector[11]; }
                set { flagVector[11] = value; }
            }
        }

        /// <summary>
        /// some extra flags that don't have a better place to go
        /// </summary>
        public PlayerFlags Flags = new PlayerFlags();

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

using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using SS.Core.Packets;

namespace SS.Core
{
    public enum ClientType
    {
        Unknown, 
        Fake, 
        Vie, 
        Continuum, 
        Chat
    }

    /** player status codes */
    public enum PlayerState
    {
        /** player was just created, and isn't ready to do anything yet */
        /* transitions to: connected */
        Uninitialized, 

        /** player is connected (key exchange completed) but has not logged
         ** in yet */
        /* transitions to: need_auth or leaving_zone */
        Connected, 

        /** player sent login, auth request will be sent */
        /* transitions to: wait_auth */
        NeedAuth, 

        /** waiting for auth response */
        /* transitions to: connected or need_global_sync */
        WaitAuth, 

        /** auth done, will request global sync */
        /* transitions to: wait_global_sync1 */
        NeedGlobalSync, 

        /** waiting for sync global persistent data to complete */
        /* transitions to: do_global_callbacks */
        WaitGlobalSync1, 

        /** global sync done, will call global player connecting callbacks */
        /* transitions to: send_login_response */
        DoGlobalCallbacks, 

        /** callbacks done, will send arena response */
        /* transitions to: loggedin */
        SendLoginResponse, 

        /** player is finished logging in but is not in an arena yet status
         ** returns here after leaving an arena, also */
        /* transitions to: do_freq_and_arena_sync or leaving_zone */
        LoggedIn, 

        /* p->arena is valid starting here */

        /** player has requested entering an arena, needs to be assigned a
         ** freq and have arena data syched */
        /* transitions to: wait_arena_sync1 (or loggedin) */
        DoFreqAndArenaSync, 

        /** waiting for scores sync */
        /* transitions to: send_arena_response (or do_arena_sync2) */
        WaitArenaSync1, 

        /** done with scores, needs to send arena response and run arena
         ** entering callbacks */
        /* transitions to: playing (or do_arena_sync2) */
        ArenaRespAndCBS, 

        /** player is playing in an arena. typically the longest state */
        /* transitions to: leaving_arena */
        Playing, 

        /** player has left arena, callbacks need to be called */
        /* transitions to: do_arena_sync2 */
        LeavingArena, 

        /** need to sync in the other direction */
        /* transitions to: wait_arena_sync2 */
        DoArenaSync2, 

        /** waiting for scores sync, other direction */
        /* transitions to: loggedin */
        WaitArenaSync2, 

        /* p->arena is no longer valid after this point */

        /** player is leaving zone, call disconnecting callbacks and start
         ** global sync */
        /* transitions to: wait_global_sync2 */
        LeavingZone, 

        /** waiting for global sync, other direction */
        /* transitions to: timewait */
        WaitGlobalSync2, 

        /** the connection is all set to be ended. the network layer will
         ** free the player after this. */
        /* transitions to: (none) */
        TimeWait
    };

    /** this encapsulates a bunch of the typical position information about
     ** players in standard clients.
     */
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

    [FlagsAttribute()]
    public enum PlayerPositionStatus
    {
        /** whether stealth is on */
        Stealth  = 1, 
        /** whether cloak is on */
        Cloak    = 2, 
        /** whether xradar is on */
        XRadar   = 4, 
        /** whether antiwarp is on */
        Antiwarp = 8, 
        /** whether to display the flashing image for a few frames */
        Flash    = 16, 
        /** whether the player is in a safezone */
        Safezone = 32, 
        /** whether the player is a ufo */
        Ufo      = 64
    }

    public class Player
    {
        // TODO: when we figure out how packets will be handled
        public PData pkt;

        public ShipType Ship
        {
            get { return (ShipType)pkt.Ship; }
        }

        public short Freq
        {
            get { return pkt.Freq; }
        }

        public readonly int Id;
        public ClientType Type = ClientType.Unknown;
        public PlayerState Status = PlayerState.Uninitialized;
        public PlayerState WhenLoggedIn = PlayerState.Uninitialized;

        public Arena Arena;
        public Arena NewArena;

        public string Name;
        public string Squad;

        public short Xres;
        public short Yres;

        public DateTime ConnectTime;

        public PlayerPosition Position;

        public uint MacId;
        public uint PermId;

        public IPAddress IpAddress;

        public string ConnectAs;
        public string ClientName;
        
        // TODO: change this to actual bits
        public struct Flags
        {
            /** if the player has been authenticated by either a billing
             ** server or a password file */
            public bool authenticated;
            /** set when the player has changed freqs or ships, but before he
             ** has acknowleged it */
            public bool during_change;
            /** if player wants optional .lvz files */
            public bool want_all_lvz;
            /** if player is waiting for db query results */
            public bool during_query;
            /** if the player's lag is too high to let him be in a ship */
            public bool no_ship;
            /** if the player's lag is too high to let him have flags or
             ** balls */
            public bool no_flags_balls;
            /** if the player has sent a position packet since entering the
             ** arena */
            public bool sent_ppk;
            /** if the player has sent a position packet with a weapon since
             ** this flag was reset */
            public bool sent_wpn;
            /** if the player is a bot who wants all position packets */
            public bool see_all_posn;
            /** if the player is a bot who wants his own position packets */
            public bool see_own_posn;
            /** if the player needs to transition to a leaving arena state
             ** while wainting for the database to return */
            public bool leave_arena_when_done_waiting;
            /** if the player's obscenity filter is on */
            public bool obscenity_filter;
            /** fill this up to 32 bits */
            //bool padding : 20;
        };
        public Flags flags;

        // used for PPD (Per Player Data)
        // TODO: consider storing this in central lookup instead of a dictionary on each player object
        private Dictionary<int, object> _playerExtraData = new Dictionary<int,object>();

        public Player(int id)
        {
            Id = id;

            // maybe change pid to short?  asss uses int all over, wonder why...
            pkt = new PData();
            pkt.Pid = (short)id;
        }

        /// <summary>
        /// Per Player Data
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public object this[int key]
        {
            get { return _playerExtraData[key]; }
            set { _playerExtraData[key] = value; }
        }

        public bool IsStandard
        {
            get { return (Type == ClientType.Vie) || (Type == ClientType.Continuum); }
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
    }
}

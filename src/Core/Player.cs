using SS.Core.Packets;
using SS.Core.Packets.S2C;
using SS.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;

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
        /// <para>transitions to: connected</para>
        /// </summary>
        Uninitialized = 0,

        /// <summary>
        /// player is connected (key exchange completed) but has not logged in yet
        /// <para>transitions to: need_auth or leaving_zone</para>
        /// </summary>
        Connected,

        /// <summary>
        /// player sent login, auth request will be sent
        /// <para>transitions to: wait_auth</para>
        /// </summary>
        NeedAuth,

        /// <summary>
        /// waiting for auth response
        /// <para>transitions to: connected or need_global_sync</para>
        /// </summary>
        WaitAuth,

        /// <summary>
        /// auth done, will request global sync
        /// <para>transitions to: wait_global_sync1</para>
        /// </summary>
        NeedGlobalSync,

        /// <summary>
        /// waiting for sync global persistent data to complete
        /// <para>transitions to: do_global_callbacks</para>
        /// </summary>
        WaitGlobalSync1,

        /// <summary>
        /// global sync done, will call global player connecting callbacks
        /// <para>transitions to: send_login_response</para>
        /// </summary>
        DoGlobalCallbacks,

        /// <summary>
        /// callbacks done, will send arena response
        /// <para>transitions to: loggedin</para>
        /// </summary>
        SendLoginResponse,

        /// <summary>
        /// Player is finished logging in but is not in an arena yet.
        /// Also, status returns here after leaving an arena.
        /// <para>transitions to: do_freq_and_arena_sync or leaving_zone</para>
        /// </summary>
        LoggedIn,

        // p->arena is valid starting here

        /// <summary>
        /// player has requested entering an arena, needs to be assigned a freq and have arena data syched
        /// <para>transitions to: wait_arena_sync1 (or loggedin)</para>
        /// </summary>
        DoFreqAndArenaSync,

        /// <summary>
        /// waiting for scores sync
        /// <para>transitions to: send_arena_response (or do_arena_sync2)</para>
        /// </summary>
        WaitArenaSync1,

        /// <summary>
        /// done with scores, needs to send arena response and run arena
        /// entering callbacks
        /// <para>transitions to: playing (or do_arena_sync2)</para>
        /// </summary>
        ArenaRespAndCBS,

        /// <summary>
        /// player is playing in an arena. typically the longest state
        /// <para>transitions to: leaving_arena</para>
        /// </summary>
        Playing,

        /// <summary>
        /// player has left arena, callbacks need to be called
        /// <para>transitions to: do_arena_sync2</para>
        /// </summary>
        LeavingArena,

        /// <summary>
        /// need to sync in the other direction
        /// <para>transitions to: wait_arena_sync2</para>
        /// </summary>
        DoArenaSync2,

        /// <summary>
        /// waiting for scores sync, other direction
        /// <para>transitions to: loggedin</para>
        /// </summary>
        WaitArenaSync2,

        // p->arena is no longer valid after this point

        /// <summary>
        /// player is leaving zone, call disconnecting callbacks and start global sync
        /// <para>transitions to: wait_global_sync2</para>
        /// </summary>
        LeavingZone,

        /// <summary>
        /// waiting for global sync, other direction
        /// <para>transitions to: timewait</para>
        /// </summary>
        WaitGlobalSync2,

        /// <summary>
        /// the connection is all set to be ended. the network layer will free the player after this.
        /// <para>transitions to: (none)</para>
        /// </summary>
        TimeWait
    };

    /// <summary>
    /// this encapsulates a bunch of the typical position information about players in standard clients.
    /// </summary>
    public class PlayerPosition
    {
        /// <summary>
        /// x coordinate of current position in pixels
        /// </summary>
        public int X;

        /// <summary>
        /// y coordinate of current position in pixels
        /// </summary>
        public int Y;

        /// <summary>
        /// velocity in positive x direction (pixels/second)
        /// </summary>
        public int XSpeed;

        /// <summary>
        /// velocity in positive y direction (pixels/second)
        /// </summary>
        public int YSpeed;

        /// <summary>
        /// rotation value (0-63)
        /// </summary>
        public int Rotation;

        /// <summary>
        /// current bounty
        /// </summary>
        public uint Bounty;

        /// <summary>
        /// status bitfield
        /// </summary>
        public PlayerPositionStatus Status;

        /// <summary>
        /// current energy
        /// </summary>
        public int Energy;

        /// <summary>
        /// time of last position packet
        /// </summary>
        public ServerTick Time;
    };

    // TODO: Investigate thread safety. Possibly the attempt is at locking all player data in the PlayerData module? Though there are places that dont?
    public class Player : IPlayerTarget
    {
        private PlayerDataPacket _packet;

        /// <summary>
        /// The <see cref="S2CPacketType.PlayerEntering"/> packet that gets sent to clients.
        /// </summary>
        public ref PlayerDataPacket Packet { get { return ref _packet; } } // TODO: maybe make this ref readonly and provide additional mutation methods/properties on the Player class?

        public ShipType Ship
        {
            get { return (ShipType)_packet.Ship; }
            set { _packet.Ship = (sbyte)value; }
        }

        public short Freq
        {
            get { return _packet.Freq; }
            set { _packet.Freq = value; }
        }

        /// <summary>
        /// ID of the player attached to (-1 means not attached).
        /// </summary>
        public short Attached
        {
            get { return _packet.AttachedTo; }
            set { _packet.AttachedTo = value; }
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
        public PlayerPosition Position = new();

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

        public ServerTick LastDeath;

        public ServerTick NextRespawn;

        public class PlayerFlags
        {
            private BitVector32 flagVector = new(0);

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

            /// <summary>
            /// if the player has died but not yet respawned
            /// </summary>
            public bool IsDead
            {
                get { return flagVector[BitVector32Masks.GetMask(12)]; }
                set { flagVector[BitVector32Masks.GetMask(12)] = value; }
            }
        }

        /// <summary>
        /// some extra flags that don't have a better place to go
        /// </summary>
        public readonly PlayerFlags Flags = new();

        /// <summary>
        /// Used to store Per Player Data (PPD). 
        /// </summary>
        /// <remarks>
        /// Using ConcurrentDictionary to allow multiple readers and 1 writer (the PlayerData module).
        /// </remarks>
        private readonly ConcurrentDictionary<int, object> _extraData = new();

        public Player(int id)
        {
            Id = id;

            // TODO: maybe change pid to short?  asss uses int all over, wonder why...
            _packet = new() { Type = (byte)S2CPacketType.PlayerEntering, PlayerId = (short)id };
        }

        /// <summary>
        /// Per Player Data
        /// </summary>
        /// <param name="key">Key from <see cref="ComponentInterfaces.IPlayerData.AllocatePlayerData{T}"/>.</param>
        /// <returns>The data or <see langword="null"/> if not found.</returns>
        public object this[int key]
        {
            get => _extraData.TryGetValue(key, out object obj) ? obj : null;

            // Only to be used by the PlayerData module.
            internal set => _extraData[key] = value;
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

        // Only to be used by the PlayerData module.
        internal void RemoveExtraData(int key)
        {
            if (_extraData.Remove(key, out object data)
                && data is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        // Only to be used by the PlayerData module.
        internal void RemoveAllExtraData()
        {
            foreach (object ppd in _extraData.Values)
            {
                if (ppd is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _extraData.Clear();
        }

        #region IPlayerTarget Members

        Player IPlayerTarget.Player
        {
            get { return this; }
        }

        #endregion

        #region ITarget Members

        TargetType ITarget.Type
        {
            get { return TargetType.Player; }
        }

        #endregion
    }
}

using SS.Core.ComponentInterfaces;
using SS.Core.Modules;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
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
    /// A key for accessing "extra data" per-player.
    /// </summary>
    /// <typeparam name="T">The type of "extra data".</typeparam>
    /// <remarks>
    /// <para>
    /// A per-player data slot is allocated using <see cref="IPlayerData.AllocatePlayerData{T}"/>, which returns a <see cref="PlayerDataKey{T}"/>.
    /// The data can then be accessed by using <see cref="Player.TryGetExtraData{T}(PlayerDataKey{T}, out T)"/> on any of the <see cref="Player"/> objects.
    /// When the data slot is no longer required, it can be freed using <see cref="IPlayerData.FreePlayerData{T}(PlayerDataKey{T})"/>.
    /// </para>
    /// <para>
    /// Modules normally allocate a slot when they are loaded and free the slot when they are unloaded.
    /// </para>
    /// </remarks>
    public readonly struct PlayerDataKey<T>
    {
        internal readonly int Id;

        /// <summary>
        /// Internal constructor that only the <see cref="PlayerData"/> module is meant to call.
        /// </summary>
        /// <param name="id">Id that uniquely identifies an "extra data" slot.</param>
        internal PlayerDataKey(int id)
        {
            Id = id;
        }
    }

    // TODO: Investigate thread safety. Possibly the attempt is at locking all player data in the PlayerData module? Though there are places that dont?
    [DebuggerDisplay("[{Name}] [pid={Id}] ({Type})")]
    public class Player : IPlayerTarget
    {
        private S2C_PlayerData _packet;

        /// <summary>
        /// The <see cref="S2CPacketType.PlayerEntering"/> packet that gets sent to clients.
        /// </summary>
        public ref S2C_PlayerData Packet { get { return ref _packet; } } // TODO: maybe make this ref readonly and provide additional mutation methods/properties on the Player class?

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
            internal set { _packet.AttachedTo = value; }
        }

        /// <summary>
        /// The player ID.
        /// </summary>
        public readonly int Id;

        internal readonly PlayerData Manager;

        /// <summary>
        /// The client type.
        /// </summary>
        public ClientType Type { get; internal set; } = ClientType.Unknown;

        /// <summary>
        /// The player's state.
        /// Core modules use this to transition a player between various stages.
        /// Other modules will mostly just care whether the player is <see cref="PlayerState.Playing"/>.
        /// </summary>
        public PlayerState Status { get; internal set; } = PlayerState.Uninitialized;

        /// <summary>
        /// The state to move to after returning to <see cref="PlayerState.LoggedIn"/>.
        /// </summary>
        internal PlayerState WhenLoggedIn = PlayerState.Uninitialized;

        private Arena _arena;

        /// <summary>
        /// The player's current arena, or <see langword="null"/> if not in an arena yet.
        /// </summary>
        public Arena Arena
        {
            get => _arena;
            internal set
            {
                if (value != null)
                    Debug.Assert(Manager.Broker == value.Manager.Broker);

                _arena = value;
            }
        }

        private Arena _newArena;

        /// <summary>
        /// The arena the player is trying to enter.
        /// </summary>
        public Arena NewArena
        {
            get => _newArena;
            internal set
            {
                if (value != null)
                    Debug.Assert(Manager.Broker == value.Manager.Broker);

                _newArena = value;
            }
        }

        public const int MaxNameLength = 24; // TODO: find out why ASSS allows longer than can fit in a PlayerDataPacket

        private string _name;

        /// <summary>
        /// The player's name.
        /// </summary>
        /// <exception cref="ArgumentException">Value cannot be null or white-space.</exception>
        /// <exception cref="ArgumentException">Value is too long.</exception>
        public string Name
        {
            get => _name;
            internal set
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException("Cannot be null or white-space.", nameof(value));

                if (StringUtils.DefaultEncoding.GetByteCount(value) > MaxNameLength - 1) // -1 for null-terminator
                    throw new ArgumentException($"Does not fit into {MaxNameLength - 1} bytes when encoded.", nameof(value));

                _name = value;
            }
        }

        public const int MaxSquadLength = 24; // TODO: find out why ASSS allows longer than can fit in a PlayerDataPacket

        private string _squad;

        /// <summary>
        /// The player's squad.
        /// </summary>
        /// <exception cref="ArgumentException">Value cannot be null or white-space.</exception>
        /// <exception cref="ArgumentException">Value is too long.</exception>
        public string Squad
        {
            get => _squad;
            internal set
            {
                if (value != null && StringUtils.DefaultEncoding.GetByteCount(value) > MaxSquadLength - 1) // -1 for null-terminator
                    throw new ArgumentException($"Does not fit into {MaxSquadLength - 1} bytes when encoded.", nameof(value));

                _squad = value;
            }
        }

        /// <summary>
        /// X screen resolution, for standard clients.
        /// </summary>
        public short Xres { get; internal set; }

        /// <summary>
        /// Y screen resolution, for standard clients.
        /// </summary>
        public short Yres { get; internal set; }

        /// <summary>
        /// The time that this player first connected.
        /// </summary>
        public DateTime ConnectTime { get; internal set; }

        /// <summary>
        /// This encapsulates a bunch of the typical position information about players in standard clients.
        /// </summary>
        public class PlayerPosition
        {
            /// <summary>
            /// x coordinate of current position in pixels
            /// </summary>
            public short X { get; internal set; }

            /// <summary>
            /// y coordinate of current position in pixels
            /// </summary>
            public short Y { get; internal set; }

            /// <summary>
            /// velocity in positive x direction (pixels/second)
            /// </summary>
            public int XSpeed { get; internal set; }

            /// <summary>
            /// velocity in positive y direction (pixels/second)
            /// </summary>
            public int YSpeed { get; internal set; }

            private sbyte rotation;

            /// <summary>
            /// rotation value (0-39)
            /// </summary>
            public sbyte Rotation
            {
                get => rotation;
                internal set
                {
                    if (value != rotation)
                    {
                        switch ((value - rotation) % 40)
                        {
                            case > 20: IsLastRotationClockwise = true; break;
                            case < 20: IsLastRotationClockwise = false; break;
                            default: break; // can't tell, complete 180 degree turn
                        }
                    }

                    rotation = value;
                }
            }

            /// <summary>
            /// Whether the last rotation moved in the clockwise direction.
            /// </summary>
            public bool IsLastRotationClockwise { get; private set; }

            /// <summary>
            /// current bounty
            /// </summary>
            public uint Bounty { get; internal set; }

            /// <summary>
            /// status bitfield
            /// </summary>
            public PlayerPositionStatus Status { get; internal set; }

            /// <summary>
            /// current energy
            /// </summary>
            public short Energy { get; internal set; }

            /// <summary>
            /// time of last position packet
            /// </summary>
            public ServerTick Time { get; internal set; }

            internal void Initialize()
            {
                X = 0;
                Y = 0;
                XSpeed = 0;
                YSpeed = 0;
                Rotation = 0;
                IsLastRotationClockwise = false;
                Bounty = 0;
                Status = 0;
                Energy = 0;
                Time = 0;
            }
        }

        /// <summary>
        /// Recent information about the player's position.
        /// </summary>
        public readonly PlayerPosition Position = new();

        /// <summary>
        /// The player's machine id, for standard clients, from the <see cref="LoginPacket"/>.
        /// </summary>
        public uint MacId { get; internal set; }

        /// <summary>
        /// Another identifier (like <see cref="MacId"/>), for standard clients, from the <see cref="LoginPacket"/>.
        /// </summary>
        public uint PermId { get; internal set; }

        /// <summary>
        /// IP address the player is connecting from.
        /// </summary>
        public IPAddress IpAddress { get; internal set; }

        /// <summary>
        /// If the player has connected through a port that sets a default arena, that will be stored here.
        /// </summary>
        public string ConnectAs { get; internal set; }

        /// <summary>
        /// A text representation of the client being used.
        /// </summary>
        public string ClientName { get; internal set; }

        /// <summary>
        /// The server recorded time of the player's last death.
        /// </summary>
        public ServerTick LastDeath { get; internal set; }

        /// <summary>
        /// When the server expects the player to respawn after dying.
        /// This is: <see cref="LastDeath"/> + Kill:EnterDelay.
        /// </summary>
        public ServerTick NextRespawn { get; internal set; }

        public class PlayerFlags
        {
            private BitVector32 flagVector = new(0);

            /// <summary>
            /// if the player has been authenticated by either a billing server or a password file
            /// </summary>
            public bool Authenticated
            {
                get { return flagVector[BitVector32Masks.GetMask(0)]; }
                internal set { flagVector[BitVector32Masks.GetMask(0)] = value; }
            }

            /// <summary>
            /// set when the player has changed freqs or ships, but before he has acknowleged it
            /// </summary>
            public bool DuringChange
            {
                get { return flagVector[BitVector32Masks.GetMask(1)]; }
                internal set { flagVector[BitVector32Masks.GetMask(1)] = value; }
            }

            /// <summary>
            /// if player wants optional .lvz files
            /// </summary>
            public bool WantAllLvz
            {
                get { return flagVector[BitVector32Masks.GetMask(2)]; }
                internal set { flagVector[BitVector32Masks.GetMask(2)] = value; }
            }

            /// <summary>
            /// if player is waiting for db query results
            /// </summary>
            public bool DuringQuery
            {
                get { return flagVector[BitVector32Masks.GetMask(3)]; }
                internal set { flagVector[BitVector32Masks.GetMask(3)] = value; }
            }

            /// <summary>
            /// if the player's lag is too high to let him be in a ship
            /// </summary>
            public bool NoShip
            {
                get { return flagVector[BitVector32Masks.GetMask(4)]; }
                internal set { flagVector[BitVector32Masks.GetMask(4)] = value; }
            }

            /// <summary>
            /// if the player's lag is too high to let him have flags or balls
            /// </summary>
            public bool NoFlagsBalls
            {
                get { return flagVector[BitVector32Masks.GetMask(5)]; }
                internal set { flagVector[BitVector32Masks.GetMask(5)] = value; }
            }

            /// <summary>
            /// if the player has sent a position packet since entering the arena
            /// </summary>
            public bool SentPositionPacket
            {
                get { return flagVector[BitVector32Masks.GetMask(6)]; }
                internal set { flagVector[BitVector32Masks.GetMask(6)] = value; }
            }

            /// <summary>
            /// if the player has sent a position packet with a weapon since this flag was reset
            /// </summary>
            public bool SentWeaponPacket
            {
                get { return flagVector[BitVector32Masks.GetMask(7)]; }
                internal set { flagVector[BitVector32Masks.GetMask(7)] = value; }
            }

            /// <summary>
            /// if the player is a bot who wants all position packets
            /// </summary>
            public bool SeeAllPositionPackets
            {
                get { return flagVector[BitVector32Masks.GetMask(8)]; }
                internal set { flagVector[BitVector32Masks.GetMask(8)] = value; }
            }

            /// <summary>
            /// if the player is a bot who wants his own position packets
            /// </summary>
            public bool SeeOwnPosition
            {
                get { return flagVector[BitVector32Masks.GetMask(9)]; }
                internal set { flagVector[BitVector32Masks.GetMask(9)] = value; }
            }

            /// <summary>
            /// if the player needs to transition to a leaving arena state while wainting for the database to return
            /// </summary>
            public bool LeaveArenaWhenDoneWaiting
            {
                get { return flagVector[BitVector32Masks.GetMask(10)]; }
                internal set { flagVector[BitVector32Masks.GetMask(10)] = value; }
            }

            /// <summary>
            /// if the player's obscenity filter is on
            /// </summary>
            public bool ObscenityFilter
            {
                get { return flagVector[BitVector32Masks.GetMask(11)]; }
                internal set { flagVector[BitVector32Masks.GetMask(11)] = value; }
            }

            /// <summary>
            /// if the player has died but not yet respawned
            /// </summary>
            public bool IsDead
            {
                get { return flagVector[BitVector32Masks.GetMask(12)]; }
                internal set { flagVector[BitVector32Masks.GetMask(12)] = value; }
            }

            internal void Initialize()
            {
                flagVector = new(0);
            }
        }

        /// <summary>
        /// Extra flags that don't have a better place to go.
        /// </summary>
        public readonly PlayerFlags Flags = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="Player"/> class with a specified PlayerID.
        /// </summary>
        /// <param name="id">The PlayerID.</param>
        /// <param name="manager">The creator.</param>
        internal Player(int id, PlayerData manager)
        {
            Id = id;
            Manager = manager ?? throw new ArgumentNullException(nameof(manager));

            Initialize();
        }

        internal void Initialize()
        {
            _packet = new() { Type = (byte)S2CPacketType.PlayerEntering, PlayerId = (short)Id };
            Ship = ShipType.Spec;
            Freq = -1;
            Attached = -1;
            Type = ClientType.Unknown;
            Status = PlayerState.Uninitialized;
            WhenLoggedIn = PlayerState.Uninitialized;
            Arena = null;
            NewArena = null;
            _name = null;
            Squad = null;
            Xres = 0;
            Yres = 0;
            ConnectTime = DateTime.UtcNow;
            ConnectAs = null;
            Position.Initialize();
            MacId = 0;
            PermId = 0;
            IpAddress = IPAddress.None;
            ConnectAs = null;
            ClientName = null;
            LastDeath = 0;
            NextRespawn = 0;
            Flags.Initialize();
            _extraData.Clear();
        }

        /// <summary>
        /// Whether the player is on a client with the ability to play (VIE Client or Continuum).
        /// </summary>
        public bool IsStandard => (Type == ClientType.VIE) || (Type == ClientType.Continuum);

        /// <summary>
        /// Whether the player is on a chat client (no ability to play).
        /// </summary>
        public bool IsChat => Type == ClientType.Chat;

        /// <summary>
        /// Whether the player is a human (as opposed to an internally controlled fake player).
        /// </summary>
        public bool IsHuman => IsStandard || IsChat;

        #region Extra Data (Per-Player Data)

        /// <summary>
        /// Used to store Per Player Data (PPD). 
        /// </summary>
        /// <remarks>
        /// Using ConcurrentDictionary to allow multiple readers and 1 writer (the PlayerData module).
        /// </remarks>
        private readonly ConcurrentDictionary<int, object> _extraData = new();

        /// <summary>
        /// Attempts to get extra data with the specified key.
        /// </summary>
        /// <typeparam name="T">The type of the data.</typeparam>
        /// <param name="key">The key of the data to get, from <see cref="IPlayerData.AllocatePlayerData{T}"/>.</param>
        /// <param name="data">The data if found and was of type <typeparamref name="T"/>. Otherwise, <see langword="null"/>.</param>
        /// <returns>True if the data was found and was of type <typeparamref name="T"/>. Otherwise, false.</returns>
        public bool TryGetExtraData<T>(PlayerDataKey<T> key, out T data) where T : class
        {
            if (_extraData.TryGetValue(key.Id, out object obj)
                && obj is T tData)
            {
                data = tData;
                return true;
            }

            data = default;
            return false;
        }

        /// <summary>
        /// Sets extra data.
        /// </summary>
        /// <remarks>Only to be used by the <see cref="PlayerData"/> module.</remarks>
        /// <param name="keyId">Id of the data to set.</param>
        /// <param name="data">The data to set.</param>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> was null.</exception>
        internal void SetExtraData(int keyId, object data)
        {
            _extraData[keyId] = data ?? throw new ArgumentNullException(nameof(data));
        }

        /// <summary>
        /// Removes extra data.
        /// </summary>
        /// <remarks>Only to be used by the <see cref="PlayerData"/> module.</remarks>
        /// <param name="keyId">Id of the data to remove.</param>
        /// <param name="data">The data removed, or the default value if nothing was removed.</param>
        /// <returns><see langword="true"/> if the data was removed; otherwise <see langword="false"/>.</returns>
        internal bool TryRemoveExtraData(int keyId, out object data)
        {
            return _extraData.TryRemove(keyId, out data);
        }

        #endregion

        // TODO: Maybe a way to synchronize?
        //public void Lock()
        //{
        //    //Manager.Broker
        //    //Arena.Manager.Broker
        //}

        #region IPlayerTarget Members

        Player IPlayerTarget.Player => this;

        #endregion

        #region ITarget Members

        TargetType ITarget.Type => TargetType.Player;

        #endregion
    }
}

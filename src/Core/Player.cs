using SS.Core.ComponentInterfaces;
using SS.Core.Modules;
using SS.Packets.Game;
using SS.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace SS.Core
{
    /// <summary>
    /// Actions that represent important events in a <see cref="Player"/>'s life-cycle.
    /// </summary>
    /// <remarks>
    /// These actions are hooked into by using the <see cref="ComponentCallbacks.PlayerActionCallback"/>.
    /// </remarks>
    public enum PlayerAction
    {
        /// <summary>
        /// The player has connected to the server (logged in).
        /// </summary>
        /// <remarks>
        /// Not arena-specific.
        /// </remarks>
        Connect,

        /// <summary>
        /// The player is disconnecting from the server.
        /// </summary>
        /// <remarks>
        /// Not arena-specific.
        /// </remarks>
        Disconnect,

        /// <summary>
        /// This is occurs at the earliest point after a player indicates their intention to enter an arena.
        /// </summary>
        /// <remarks>
        /// In general it's better to use <see cref="EnterArena"/> for logic that should happen on entering arenas.
        /// However, is useful in cases where a module needs to do processing prior to <see cref="EnterArena"/>, 
        /// so that it's ready for another module to call it during <see cref="EnterArena"/>.
        /// </remarks>
        PreEnterArena,

        /// <summary>
        /// The player is entering an arena.
        /// </summary>
        EnterArena,

        /// <summary>
        /// The player is leaving an arena.
        /// </summary>
        LeaveArena,

        /// <summary>
        /// This is called at some point after <see cref="EnterArena"/>, when the player has sent their first position packet.
        /// This indicates that the player has joined the game, as opposed to still downloading map or lvz files.
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
    /// States of a <see cref="Player"/>'s life-cycle.
    /// </summary>
    /// <remarks>
    /// In general, most modules should NOT need to use this.
    /// Instead, use the <see cref="ComponentCallbacks.PlayerActionCallback"/> to hook into the events of a <see cref="Player"/>'s life-cycle.
    /// </remarks>
    public enum PlayerState
    {
        /// <summary>
        /// The player was just created, and isn't ready to do anything yet.
        /// </summary>
        /// <remarks>
        /// Transitions to: <see cref="Connected"/>.
        /// </remarks>
        Uninitialized = 0,

        /// <summary>
        /// The player is connected (key exchange completed) but has not logged in yet.
        /// </summary>
        /// <remarks>
        /// Transitions to: <see cref="NeedAuth"/> or <see cref="LeavingZone"/>.
        /// </remarks>
        Connected,

        /// <summary>
        /// The player sent login information. An authentication request will be sent.
        /// </summary>
        /// <remarks>
        /// Transitions to: <see cref="WaitAuth"/>.
        /// </remarks>
        NeedAuth,

        /// <summary>
        /// Waiting for an authentication response.
        /// </summary>
        /// <remarks>
        /// Transitions to: <see cref="Connected"/> or <see cref="NeedGlobalSync"/>.
        /// </remarks>
        WaitAuth,

        /// <summary>
        /// Authentication was successful. Will request global persistent data for the player to be loaded.
        /// </summary>
        /// <remarks>
        /// Transitions to: <see cref="WaitGlobalSync1"/>.
        /// </remarks>
        NeedGlobalSync,

        /// <summary>
        /// Waiting for global persistent data to be loaded.
        /// </summary>
        /// <remarks>
        /// Transitions to: <see cref="DoGlobalCallbacks"/>.
        /// </remarks>
        WaitGlobalSync1,

        /// <summary>
        /// The player's global persistent data has been loaded.
        /// Will call global player connecting callbacks.
        /// </summary>
        /// <remarks>
        /// Transitions to: <see cref="WaitConnectHolds"/>.
        /// </remarks>
        DoGlobalCallbacks,

        /// <summary>
        /// Waits for holds that were placed when <see cref="ComponentCallbacks.PlayerActionCallback"/> (<see cref="PlayerAction.Connect"/>) was called.
        /// </summary>
        /// <remarks>Transitions to: <see cref="SendLoginResponse"/></remarks>
        WaitConnectHolds,

        /// <summary>
        /// Callbacks done, will send arena response.
        /// </summary>
        /// <remarks>
        /// Transitions to: <see cref="LoggedIn"/>.
        /// </remarks>
        SendLoginResponse,

        /// <summary>
        /// The player is finished logging in but is not in an arena yet.
        /// Also, status returns here after leaving an arena.
        /// </summary>
        /// <remarks>Transitions to: <see cref="DoFreqAndArenaSync"/> (or <see cref="LeavingZone"/>).</remarks>
        LoggedIn,

        // Player.Arena is valid starting here

        /// <summary>
        /// The player has requested to enter an arena, needs to be assigned a freq and have arena data synced.
        /// </summary>
        /// <remarks>
        /// Transitions to: <see cref="WaitArenaSync1"/> (or <see cref="LoggedIn"/>).
        /// </remarks>
        DoFreqAndArenaSync,

        /// <summary>
        /// Waiting for scores sync.
        /// </summary>
        /// <remarks>
        /// Transitions to: <see cref="ArenaRespAndCBS"/> (or <see cref="DoArenaSync2"/>).
        /// </remarks>
        WaitArenaSync1,

        /// <summary>
        /// Done with scores, needs to send arena response and run arena entering callbacks.
        /// </summary>
        /// <remarks>Transitions to: <see cref="Playing"/> (or <see cref="DoArenaSync2"/>).</remarks>
        ArenaRespAndCBS,

        // TODO: add WaitEnterArenaHolds

        /// <summary>
        /// The player is playing in an arena. This is typically the longest state.
        /// </summary>
        /// <remarks>Transitions to: <see cref="LeavingArena"/>.</remarks>
        Playing,

        /// <summary>
        /// The player has left arena, callbacks need to be called.
        /// </summary>
        /// <remarks>Transitions to: <see cref="DoArenaSync2"/>.</remarks>
        LeavingArena,

        // TODO: add WaitLeaveArenaHolds

        /// <summary>
        /// Need to sync in the other direction.
        /// </summary>
        /// <remarks>Transitions to: <see cref="WaitArenaSync2"/>.</remarks>
        DoArenaSync2,

        /// <summary>
        /// Waiting for scores sync, other direction.
        /// </summary>
        /// <remarks>Transitions to: <see cref="LoggedIn"/>.</remarks>
        WaitArenaSync2,

        // Player.Arena is no longer valid after this point

        /// <summary>
        /// The player is leaving the zone, call disconnecting callbacks and start global sync.
        /// </summary>
        /// <remarks>Transitions to: <see cref="WaitDisconnectHolds"/>.</remarks>
        LeavingZone,

        /// <summary>
        /// Waits for holds that were placed when <see cref="ComponentCallbacks.PlayerActionCallback"/> (<see cref="PlayerAction.Disconnect"/>) was called.
        /// When there are no holds remaining, start global sync (tell the persist module to save global data for the player).
        /// </summary>
        /// <remarks>Transitions to: <see cref="WaitGlobalSync2"/></remarks>
        WaitDisconnectHolds,

        /// <summary>
        /// Waiting for global sync, other direction.
        /// </summary>
        /// <remarks>Transitions to: <see cref="TimeWait"/>.</remarks>
        WaitGlobalSync2,

        /// <summary>
        /// The connection is all set to be ended. The network layer will free the player after this.
        /// </summary>
        /// <remarks>Transitions to: <see cref="Uninitialized"/>.</remarks>
        TimeWait
    };

    public enum ConnectionType
    {
        Unknown = 0,
        SlowModem = 1,
        FastModem = 2,
        UnknownModem = 3,
        UnknownNotRAS = 4,
    }

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

    /// <summary>
    /// Represents a player.
    /// </summary>
    /// <remarks>
    /// A player normally is a person connected to the server using a game client or chat client.
    /// However, it can also be a 'fake' player. There are many possible uses of fake players, 
    /// such as when playing back a replay, bots, or AI player bots.
    /// </remarks>
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
        /// Which additional non-VIE client features the client supports.
        /// </summary>
        public ClientFeatures ClientFeatures { get; internal set; }

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

        private Arena? _arena;

        /// <summary>
        /// The player's current arena, or <see langword="null"/> if not in an arena yet.
        /// </summary>
        public Arena? Arena
        {
            get => _arena;
            internal set
            {
                if (value != null)
                    Debug.Assert(Manager.Broker == value.Manager.Broker);

                _arena = value;
            }
        }

        private Arena? _newArena;

        /// <summary>
        /// The arena the player is trying to enter.
        /// </summary>
        public Arena? NewArena
        {
            get => _newArena;
            internal set
            {
                if (value != null)
                    Debug.Assert(Manager.Broker == value.Manager.Broker);

                _newArena = value;
            }
        }

        private string? _name;

        /// <summary>
        /// The player's name.
        /// </summary>
        public string? Name
        {
            get => _name;
            internal set
            {
                if (value is not null && value.Length > Constants.MaxPlayerNameLength)
                {
                    value = value[..Constants.MaxPlayerNameLength];
                }

                _name = value;
            }
        }

        private string? _squad;

        /// <summary>
        /// The player's squad.
        /// </summary>
        public string? Squad
        {
            get => _squad;
            internal set
            {
                if (value is not null && value.Length > Constants.MaxSquadNameLength)
                {
                    value = value[..Constants.MaxSquadNameLength];
                }

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
        /// The connection type reported by client.
        /// </summary>
        public ConnectionType ConnectionType { get; internal set; }

        /// <summary>
        /// The # of minutes from UTC.
        /// </summary>
        public short TimeZoneBias { get; internal set; }

        /// <summary>
        /// The player's machine id, for standard clients, from the <see cref="LoginPacket"/>.
        /// </summary>
        public uint MacId { get; internal set; }

        /// <summary>
        /// Another identifier (like <see cref="MacId"/>), for standard clients, from the <see cref="LoginPacket"/>.
        /// </summary>
        public uint PermId { get; internal set; }

        /// <summary>
        /// The IPv4 address of the server, reported by the client.
        /// </summary>
        public uint ClientReportedServerIPv4Address;

        /// <summary>
        /// The port that the client says it's using.
        /// </summary>
        public ushort ClientReportedBoundPort;

        /// <summary>
        /// IP address the player is connecting from.
        /// </summary>
        public IPAddress? IPAddress { get; internal set; }

        /// <summary>
        /// If the player has connected through a port that sets a default arena, that will be stored here.
        /// </summary>
        public string? ConnectAs { get; internal set; }

        /// <summary>
        /// A text representation of the client being used.
        /// </summary>
        public string? ClientName { get; internal set; }

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
            /// This is set when the player has changed freqs or ships, but before he has acknowledged it.
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
            /// if the player needs to transition to a leaving arena state while waiting for the database to return
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
        /// The number of "holds" on the player, which prevent the player from proceeding to the next stage in the player life-cycle.
        /// </summary>
        /// <remarks>
        /// This should only be modified using <see cref="IPlayerData.AddHold(Player)"/> and <see cref="IPlayerData.RemoveHold(Player)"/>.
        /// </remarks>
        internal int Holds = 0;

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
            ClientFeatures = ClientFeatures.None;
            Status = PlayerState.Uninitialized;
            WhenLoggedIn = PlayerState.Uninitialized;
            Arena = null;
            NewArena = null;
            Name = null;
            Squad = null;
            Xres = 0;
            Yres = 0;
            ConnectTime = DateTime.UtcNow;
            ConnectAs = null;
            Position.Initialize();
            ConnectionType = ConnectionType.Unknown;
            TimeZoneBias = 0;
            MacId = 0;
            PermId = 0;
            ClientReportedServerIPv4Address = 0;
            ClientReportedBoundPort = 0;
            IPAddress = IPAddress.None;
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
        private readonly ConcurrentDictionary<int, object> _extraData = new(-1, Constants.TargetPlayerExtraDataCount);

        /// <summary>
        /// Attempts to get extra data with the specified key.
        /// </summary>
        /// <typeparam name="T">The type of the data.</typeparam>
        /// <param name="key">The key of the data to get, from <see cref="IPlayerData.AllocatePlayerData{T}"/>.</param>
        /// <param name="data">The data if found and was of type <typeparamref name="T"/>. Otherwise, <see langword="null"/>.</param>
        /// <returns>True if the data was found and was of type <typeparamref name="T"/>. Otherwise, false.</returns>
        public bool TryGetExtraData<T>(PlayerDataKey<T> key, [MaybeNullWhen(false)] out T data) where T : class
        {
            if (_extraData.TryGetValue(key.Id, out object? obj)
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
        internal bool TryRemoveExtraData(int keyId, [MaybeNullWhen(false)] out object data)
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

        public override int GetHashCode()
        {
            return Id;
        }
    }
}

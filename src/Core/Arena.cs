using CommunityToolkit.HighPerformance.Buffers;
using SS.Core.ComponentInterfaces;
using SS.Core.Modules;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;

namespace SS.Core
{
    /// <summary>
    /// Actions that represent important events in an <see cref="Arena"/>'s life-cycle.
    /// </summary>
    /// <remarks>
    /// These actions are hooked into by using the <see cref="ComponentCallbacks.ArenaActionCallback"/>.
    /// </remarks>
    public enum ArenaAction
    {
        /// <summary>
        /// When an arena is created.
        /// </summary>
        /// <remarks>
        /// If the work cannot be completed synchronously, a "hold" can be placed on the arena by using <see cref="IArenaManager.AddHold(Arena)"/> and when done, released with <see cref="IArenaManager.RemoveHold(Arena)"/>.
        /// Holding an arena, prevents the arena from moving onto further steps in the life-cycle, until all holds are cleared.
        /// </remarks>
        Create,

        /// <summary>
        /// When an arena.conf file changes.
        /// </summary>
        ConfChanged,

        /// <summary>
        /// When an arena is being destroyed.
        /// </summary>
        /// <remarks>
        /// If the work cannot be completed synchronously, a "hold" can be placed on the arena by using <see cref="IArenaManager.AddHold(Arena)"/> and when done, released with <see cref="IArenaManager.RemoveHold(Arena)"/>.
        /// Holding an arena, prevents the arena from moving onto further steps in the life-cycle, until all holds are cleared.
        /// </remarks>
        Destroy,

        /// <summary>
        /// A really early step in the arena creation process. The arena is being initialized and is not yet fully created. This happens before modules are attached to the arena.
        /// </summary>
        /// <remarks>
        /// If the work cannot be completed synchronously, a "hold" can be placed on the arena by using <see cref="IArenaManager.AddHold(Arena)"/> and when done, released with <see cref="IArenaManager.RemoveHold(Arena)"/>.
        /// Holding an arena, prevents the arena from moving onto further steps in the life-cycle, until all holds are cleared.
        /// </remarks>
        PreCreate,

        /// <summary>
        /// After an arena has been destroyed.
        /// </summary>
        PostDestroy
    };

    /// <summary>
    /// States of an <see cref="Arena"/>'s life-cycle.
    /// </summary>
    /// <remarks>
    /// In general, most modules should NOT need to use this.
    /// Instead, use the <see cref="ComponentCallbacks.ArenaActionCallback"/> to hook into the events of an <see cref="Arena"/>'s life-cycle.
    /// </remarks>
    public enum ArenaState
    {
        /// <summary>
        /// The arena was just constructed.
        /// </summary>
        Uninitialized,

        /// <summary>
        /// The arena is being initialized. Either someone wants to enter the arena or it's a permanent arena.
        /// The arena.conf file is loaded and made available on the arena, <see cref="Arena.Cfg"/>.
        /// If it fails to load the arena.conf, it transitions to <see cref="Destroyed"/>.
        /// When the arena.conf loading is complete, it transitions to <see cref="WaitHolds0"/>
        /// and the <see cref="ComponentCallbacks.ArenaActionCallback"/> (<see cref="ArenaAction.PreCreate"/>) is called.
        /// </summary>
        DoInit0,

        /// <summary>
        /// Waits for modules to complete the work they started when <see cref="ComponentCallbacks.ArenaActionCallback"/> (<see cref="ArenaAction.PreCreate"/>) was called.
        /// When there are no more holds on the arena, it transitions to <see cref="DoInit1"/>.
        /// </summary>
        WaitHolds0,

        /// <summary>
        /// Modules are attached to the arena (Modules:AttachModules setting in arena.conf).
        /// When module attaching is complete, it transitions to <see cref="WaitHolds1"/>
        /// and the <see cref="ComponentCallbacks.ArenaActionCallback"/> (<see cref="ArenaAction.Create"/>) is called. 
        /// </summary>
        DoInit1,

        /// <summary>
        /// Waits for modules to complete the work they started when <see cref="ComponentCallbacks.ArenaActionCallback"/> (<see cref="ArenaAction.Create"/>) was called.
        /// When there are no more holds on the arena, it transitions to <see cref="DoInit2"/>.
        /// </summary>
        WaitHolds1,

        /// <summary>
        /// If persist module is loaded (<see cref="IPersist"/> available), it transitions to <see cref="WaitSync1"/> and tells the persist module to load persistent data for the arena.
        /// Otherwise, it transitions to <see cref="Running"/>.
        /// </summary>
        DoInit2,

        /// <summary>
        /// Waits for the persist module to complete loading persistent data for the arena.
        /// When complete, it transitions to <see cref="Running"/>.
        /// </summary>
        WaitSync1,

        /// <summary>
        /// The arena is fully created.
        /// The <see cref="Modules.Core"/> module can now send the arena responses to players waiting to enter this arena.
        /// This is the state that an arena should be in for most of its life.
        /// When the arena is to be recycled, it transitions to <see cref="Closing"/>.
        /// When the arena no longer has any players in it, and it's not a permanent arena, it transitions to <see cref="DoWriteData"/>.
        /// </summary>
        Running,

        /// <summary>
        /// The arena is closing because it's being recycled.
        /// It isn't accepting new players. It's waiting for the <see cref="Modules.Core"/> module to remove players from the arena.
        /// When there are no more players in the arena, it transitions to <see cref="DoWriteData"/>.
        /// </summary>
        Closing,

        /// <summary>
        /// The arena is being reaped (torn down).
        /// If persist module is loaded (<see cref="IPersist"/> available), it transitions to <see cref="WaitSync2"/> and tells the persist module to save persistent data for the arena.
        /// Otherwise, it transitions to <see cref="DoDestroy1"/>.
        /// </summary>
        DoWriteData,

        /// <summary>
        /// Waits for the persist module to complete saving persistent data for the arena.
        /// When complete, it transitions to <see cref="DoDestroy1"/>.
        /// </summary>
        WaitSync2,

        /// <summary>
        /// Transitions to <see cref="WaitHolds2"/> and the <see cref="ComponentCallbacks.ArenaActionCallback"/> (<see cref="ArenaAction.Destroy"/>) is called.
        /// </summary>
        DoDestroy1,

        /// <summary>
        /// Waits for modules to complete the work they started when <see cref="ComponentCallbacks.ArenaActionCallback"/> (<see cref="ArenaAction.Destroy"/>) was called.
        /// When there are no more holds on the arena, it transitions to <see cref="DoDestroy2"/>.
        /// </summary>
        WaitHolds2,

        /// <summary>
        /// Detaches modules that were attached to the arena.
        /// When detaching of modules is complete, the <see cref="ComponentCallbacks.ArenaActionCallback"/> (<see cref="ArenaAction.PostDestroy"/>) is called.
        /// If the arena was being recycled, it will transition back to <see cref="DoInit0"/>.
        /// Otherwise, the arena is finally removed and is transitioned to <see cref="Destroyed"/>.
        /// </summary>
        DoDestroy2,

        /// <summary>
        /// The arena has been destroyed.
        /// </summary>
        Destroyed,
    }

    /// <summary>
    /// A key for accessing "extra data" per-arena.
    /// </summary>
    /// <typeparam name="T">The type of "extra data".</typeparam>
    /// <remarks>
    /// <para>
    /// A per-arena data slot is allocated using <see cref="IArenaManager.AllocateArenaData{T}"/>, which returns a <see cref="ArenaDataKey{T}"/>.
    /// The data can then be accessed by using <see cref="Arena.TryGetExtraData{T}(ArenaDataKey{T}, out T)"/> on any of the <see cref="Arena"/> objects.
    /// When the data slot is no longer required, it can be freed using <see cref="IArenaManager.FreeArenaData{T}(ArenaDataKey{T})"/>.
    /// </para>
    /// <para>
    /// Modules normally allocate a slot when they are loaded and free the slot when they are unloaded.
    /// </para>
    /// </remarks>
    public readonly struct ArenaDataKey<T>
    {
        internal readonly int Id;

        /// <summary>
        /// Internal constructor, only the <see cref="ArenaManager"/> module is meant to create it.
        /// </summary>
        /// <param name="id">Id that uniquely identifies an "extra data" slot.</param>
        internal ArenaDataKey(int id)
        {
            Id = id;
        }
    }

    [DebuggerDisplay("{Name} ({Status})")]
    public class Arena : ComponentBroker, IArenaTarget
    {
        public const int DefaultSpecFreq = ConfigHelp.Constants.Arena.Team.SpectatorFrequency.Default;

        private static readonly char[] _digitChars = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9'];

        private ArenaState _status = ArenaState.Uninitialized;

        /// <summary>
        /// The arena's state.
        /// </summary>
        /// <remarks>
        /// The <see cref="ArenaManager"/> transitions an arena through various states.
        /// Most modules will just care if the arena's is <see cref="ArenaState.Running"/>.
        /// </remarks>
        public ArenaState Status
        {
            get => _status;
            internal set
            {
                _status = value;
                Manager.ProcessStateChange(this);
            }
        }

        internal readonly ArenaManager Manager;

        /// <summary>
        /// The full name of the arena.
        /// </summary>
        public readonly string Name;

        /// <summary>
        /// The name of the arena, minus any trailing digits.
        /// The basename is used in many places to allow easy sharing of
        /// settings and things among copies of one basic arena.
        /// </summary>
        public readonly string BaseName;

        /// <summary>
        /// The number part of the arena name. 0 when there is no number.
        /// For public arenas, the entire name is the number.
        /// </summary>
        public readonly int Number;

        /// <summary>
        /// A handle to the main config file for this arena.
        /// </summary>
        public ConfigHandle? Cfg { get; internal set; }

        /// <summary>
        /// The frequency for spectators in this arena.
        /// </summary>
        public short SpecFreq { get; internal set; } = DefaultSpecFreq;

        #region Player Counts

        private int _total = 0;
        private int _playing = 0;
        private readonly Lock _playerCountsLock = new();

        /// <summary>
        /// Get the player counts for the arena.
        /// </summary>
        /// <remarks>
        /// Refreshed by <see cref="IArenaManager.GetPopulationSummary"/>.
        /// </remarks>
        /// <param name="total">How many players total are in this arena.</param>
        /// <param name="playing">How many players are in ships in this arena.</param>
        public void GetPlayerCounts(out int total, out int playing)
        {
            lock (_playerCountsLock)
            {
                total = _total;
                playing = _playing;
            }
        }

        internal void SetPlayerCounts(int total, int playing)
        {
            lock (_playerCountsLock)
            {
                _total = total;
                _playing = playing;
            }
        }

        #endregion

        /// <summary>
        /// Whether this arena should not be destroyed when there are no players inside it.
        /// </summary>
        internal bool KeepAlive;

        /// <summary>
        /// Initializes a new instance of the <see cref="Arena"/> class with a specified name.
        /// </summary>
        /// <param name="parent">The parent broker.</param>
        /// <param name="name">The name of the arena.</param>
        /// <param name="manager">The creator.</param>
        internal Arena(IComponentBroker parent, string name, ArenaManager manager) : base(parent)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            Debug.Assert(parent == manager.Broker);
            Manager = manager ?? throw new ArgumentNullException(nameof(manager));

            Name = name;
            ReadOnlySpan<char> baseNameSpan = name.AsSpan().TrimEnd(_digitChars);
            if (baseNameSpan.IsEmpty)
            {
                BaseName = Constants.ArenaGroup_Public;
                IsPublic = true;
                Number = int.Parse(name);
            }
            else
            {
                BaseName = StringPool.Shared.GetOrAdd(baseNameSpan);
                ReadOnlySpan<char> numberStr = Name.AsSpan(BaseName.Length);
                if (numberStr.IsWhiteSpace()
                    || !int.TryParse(numberStr, out int number))
                {
                    Number = 0;
                }
                else
                {
                    Number = number;
                }
            }

            IsPrivate = Name[0] == '#';
        }

        /// <summary>
        /// This will tell you if an arena is considered a "public" arena.
        /// </summary>
        public readonly bool IsPublic;

        /// <summary>
        /// Whether the arena is private (name starts with #).
        /// </summary>
        public readonly bool IsPrivate;

        #region Extra Data (Per-Arena Data)

        /// <summary>
        /// Used to store Per Arena Data.
        /// </summary>
        private readonly ConcurrentDictionary<int, object> _extraData = new(-1, Constants.TargetArenaExtraDataCount);

        /// <summary>
        /// Attempts to get extra data with the specified key.
        /// </summary>
        /// <typeparam name="T">The type of the data.</typeparam>
        /// <param name="key">The key of the data to get, from <see cref="IPlayerData.AllocatePlayerData{T}"/>.</param>
        /// <param name="data">The data if found and was of type <typeparamref name="T"/>. Otherwise, <see langword="null"/>.</param>
        /// <returns>True if the data was found and was of type <typeparamref name="T"/>. Otherwise, false.</returns>
        public bool TryGetExtraData<T>(ArenaDataKey<T> key, [MaybeNullWhen(false)] out T data) where T : class
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
        /// <remarks>Only to be used by the <see cref="ArenaManager"/> module.</remarks>
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
        /// <remarks>Only to be used by the <see cref="ArenaManager"/> module.</remarks>
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
        //    //Manager.
        //    //Manager.Broker
        //}

        #region IArenaTarget Members

        Arena IArenaTarget.Arena => this;

        #endregion

        #region ITarget Members

        TargetType ITarget.Type => TargetType.Arena;

        #endregion

        #region Team Target

        /// <summary>
        /// Dictionary of immutable TeamTarget objects that can be reused.
        /// This is to reduce allocations (e.g. rather than allocate a new one each time a team target is needed).
        /// </summary>
        private readonly ConcurrentDictionary<short, TeamTarget> _teamTargets = new();

        public TeamTarget GetTeamTarget(short freq) => _teamTargets.GetOrAdd(freq, (f) => new TeamTarget(this, f));

        public void CleanupTeamTargets()
        {
            if (_teamTargets.IsEmpty)
                return;

            IPlayerData? playerData = GetInterface<IPlayerData>();

            if (playerData != null)
            {
                try
                {
                    playerData.Lock();

                    try
                    {
                        // TODO: The ConcurrentDictionary enumerator is not a struct, it allocates an object.
                        // Maybe change this to a regular Dictionary + locking, but then can't remove while iterating,
                        // would need another collection of type int to store the IDs to remove in.
                        // So the collection of ints would then need to come from a pool, otherwise there would be an allocation.
                        // Or maybe stackalloc an array + keep track of a count, as there shouldn't be that many teams in the first place.
                        foreach (var team in _teamTargets)
                        {
                            short freq = team.Key;

                            if (!HasPlayerOnFreq(playerData, this, freq))
                                _teamTargets.TryRemove(freq, out _);
                        }
                    }
                    finally
                    {
                        playerData.Unlock();
                    }
                }
                finally
                {
                    ReleaseInterface(ref playerData);
                }
            }

            static bool HasPlayerOnFreq(IPlayerData playerData, Arena arena, int freq)
            {
                foreach (Player p in playerData.Players)
                {
                    if (p.Arena == arena && p.Freq == freq)
                        return true;
                }

                return false;
            }
        }

        #endregion

        public override string ToString()
        {
            return Name;
        }

        /// <summary>
        /// Constructs an arena name from a base name and number.
        /// </summary>
        /// <param name="baseName">The base name of the arena.</param>
        /// <param name="number">The number of the arena.</param>
        /// <returns>The arena name.</returns>
        public static string CreateArenaName(string baseName, int number)
        {
            if (number < 0)
                throw new ArgumentOutOfRangeException(nameof(number), "Cannot be negative.");

            if (string.IsNullOrWhiteSpace(baseName) || string.Equals(baseName, Constants.ArenaGroup_Public, StringComparison.OrdinalIgnoreCase))
                return number.ToString(CultureInfo.InvariantCulture);

            return (number == 0) ? baseName : $"{baseName}{number}";
        }

        /// <summary>
        /// Constructs an arena name from a base name and number.
        /// </summary>
        /// <param name="destination">The span which the arena name should be written to.</param>
        /// <param name="baseName">The base name of the arena.</param>
        /// <param name="number">The number of the arena.</param>
        /// <param name="charsWritten">When this method returns, contains the number of characters written to <paramref name="destination"/>.</param>
        /// <returns><see langword="true"/> if the entire arena name could be written; otherwise <see langword="false"/>.</returns>
        public static bool TryCreateArenaName(Span<char> destination, ReadOnlySpan<char> baseName, int number, out int charsWritten)
        {
            if (number < 0)
                throw new ArgumentOutOfRangeException(nameof(number), "Cannot be negative.");

            if (baseName.IsWhiteSpace() || baseName.Equals(Constants.ArenaGroup_Public, StringComparison.OrdinalIgnoreCase))
                return destination.TryWrite($"{number}", out charsWritten);

            return number == 0
                ? destination.TryWrite($"{baseName}", out charsWritten)
                : destination.TryWrite($"{baseName}{number}", out charsWritten);
        }

        /// <summary>
        /// Parses an arena name into a base name and number.
        /// </summary>
        /// <param name="arenaName">The arena name to parse.</param>
        /// <param name="baseName">When this method returns, the base arena name, if parsing succeeded. <see cref="ReadOnlySpan{char}.Empty"/> if parsing failed.</param>
        /// <param name="number">When this method returns, the arena number, if parsing succeeded. Zero if parsing failed.</param>
        /// <returns><see langword="true"/> if <paramref name="arenaName"/> parsed successfully; otherwise <see langword="false"/>.</returns>
        public static bool TryParseArenaName(ReadOnlySpan<char> arenaName, out ReadOnlySpan<char> baseName, out int number)
        {
            arenaName = arenaName.Trim();
            baseName = arenaName.TrimEnd("1234567890");
            ReadOnlySpan<char> numberChars = arenaName[baseName.Length..];

            if (baseName.IsEmpty && numberChars.IsEmpty)
            {
                baseName = [];
                number = 0;
                return false;
            }

            if (baseName.IsEmpty)
                baseName = Constants.ArenaGroup_Public;

            if (numberChars.IsEmpty)
            {
                number = 0;
                return true;
            }
            else
            {
                bool success = int.TryParse(numberChars, out number);
                if (!success)
                {
                    baseName = [];
                }

                return success;
            }
        }
    }
}

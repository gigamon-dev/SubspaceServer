using SS.Core.ComponentInterfaces;
using SS.Core.Modules;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SS.Core
{
    public enum ArenaState
    {
        /// <summary>
        /// someone wants to enter the arena. first, the config file must be loaded, callbacks called
        /// </summary>
        DoInit0,

        /// <summary>
        /// waiting for first round of callbacks
        /// </summary>
        WaitHolds0, 

        /// <summary>
        /// attaching and more callbacks
        /// </summary>
        DoInit1, 

        /// <summary>
        /// waiting on modules to do init work.
        /// </summary>
        WaitHolds1, 

        /// <summary>
        /// load persistent data.
        /// </summary>
        DoInit2, 

        /// <summary>
        /// waiting on the database
        /// </summary>
        WaitSync1,

        /// <summary>
        /// now the arena is fully created. core can now send the arena 
        /// responses to players waiting to enter this arena
        /// </summary>
        Running,

        /// <summary>
        /// the arena is running for a little while, but isn't accepting new players
        /// </summary>
        Closing,

        /// <summary>
        /// the arena is being reaped, first put info in database
        /// </summary>
        DoWriteData, 

        /// <summary>
        /// waiting on the database to finish before we can unregister modules...
        /// </summary>
        WaitSync2, 

        /// <summary>
        /// arena destroy callbacks.
        /// </summary>
        DoDestroy1, 

        /// <summary>
        /// waiting for modules to do destroy work.
        /// </summary>
        WaitHolds2, 

        /// <summary>
        /// finish destroy process.
        /// </summary>
        DoDestroy2
    }

    public enum ArenaAction
    {
        /// <summary>when arena is created</summary>
	    Create,

	    /// <summary>when config file changes</summary>
        ConfChanged,

        /// <summary>when the arena is destroyed</summary>
        Destroy, 
	    
        /// <summary>really really early</summary>
        PreCreate, 

	    /// <summary>really really late</summary>
        PostDestroy
    };

    public class Arena : ComponentBroker, IArenaTarget
    {
        public const int DefaultSpecFreq = 8025;

        /// <summary>
        /// The arena's state.
        /// </summary>
        /// <remarks>
        /// The <see cref="ArenaManager"/> transitions an arena through various states.
        /// Most modules will just care if the arena's is <see cref="ArenaState.Running"/>.
        /// </remarks>
	    public ArenaState Status { get; internal set; } = ArenaState.DoInit0;

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
        /// A handle to the main config file for this arena.
        /// </summary>
        public ConfigHandle Cfg { get; internal set; }

        /// <summary>
        /// The frequency for spectators in this arena.
        /// </summary>
        [ConfigHelp("Team", "SpectatorFrequency", ConfigScope.Arena, typeof(int), Range = "0-9999", DefaultValue = "8025",
            Description = "The frequency that spectators are assigned to, by default.")]
        public short SpecFreq { get; internal set; } = DefaultSpecFreq;

	    /// <summary>
	    /// How many players are in ships in this arena.
	    /// </summary>
        /// <remarks>
        /// Refreshed by <see cref="IArenaManager.GetPopulationSummary"/>.
        /// </remarks>
	    public int Playing { get; internal set; }

        /// <summary>
        /// How many players total are in this arena.
        /// </summary>
        /// <remarks>
        /// Refreshed by <see cref="IArenaManager.GetPopulationSummary"/>.
        /// </remarks>
        public int Total { get; internal set; }

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
        internal Arena(ComponentBroker parent, string name, ArenaManager manager) : base(parent)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Cannot be null or white-space.", nameof(name));

            Debug.Assert(parent == manager.Broker);
            Manager = manager ?? throw new ArgumentNullException(nameof(manager));

            Name = name;
            BaseName = name.TrimEnd(new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
            if (string.IsNullOrEmpty(BaseName))
            {
                BaseName = Constants.ArenaGroup_Public;
                IsPublic = true;
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
        private readonly ConcurrentDictionary<int, object> _extraData = new();

        /// <summary>
        /// Per Arena Data
        /// </summary>
        /// <param name="key">Key from <see cref="IArenaManager.AllocateArenaData{T}"/>.</param>
        /// <returns></returns>
        public object this[int key]
        {
            get => _extraData.TryGetValue(key, out object obj) ? obj : null;

            // Only to be used by the ArenaManager module.
            internal set => _extraData[key] = value;
        }

        /// <summary>
        /// Removes per-arena data for a single key.
        /// </summary>
        /// <remarks>Only to be used by the <see cref="ArenaManager"/> module.</remarks>
        /// <param name="key">The key, from <see cref="IArenaManager.AllocateArenaData{T}"/>, of the per-arena data to remove.</param>
        internal void RemoveExtraData(int key)
        {
            if (_extraData.TryRemove(key, out object data)
                && data is IDisposable disposable)
            {
                disposable.Dispose();
            }            
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
        private readonly ConcurrentDictionary<int, TeamTarget> _teamTargets = new();

        public TeamTarget GetTeamTarget(int freq) => _teamTargets.GetOrAdd(freq, (f) => new TeamTarget(this, f));

        public void CleanupTeamTargets()
        {
            if (_teamTargets.Count == 0)
                return;

            IPlayerData playerData = GetInterface<IPlayerData>();

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
                            int freq = team.Key;

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
                foreach (Player p in playerData.PlayerList)
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
    }
}

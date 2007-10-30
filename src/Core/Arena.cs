using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core
{
    public interface IModuleArenaAttachable
    {
        void AttachModule(Arena arena);
        void DetachModule(Arena arena);
    }

    public enum ArenaState
    {
        /// <summary>
        /// someone wants to enter the arena. first, the config file must be
        /// loaded, callbacks called, and the persistant data loaded.
        /// </summary>
        DoInit,

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
        /// ...then unload the config file. status returns to free after this.
        /// </summary>
        DoDeinit
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

    public class Arena : ComponentBroker
    {
        private ModuleManager _mm;

        private const string PUBLIC = "(public)";
        private const string GLOBAL = "(global)";

        private const int MAX_ARENA_NAME_LENGTH = 16;

        public const int DEFAULT_SPEC_FREQ = 8025;

        /** what state the arena is in. @see ARENA_DO_INIT, etc. */
	    public ArenaState Status;
	    /** the full name of the arena */
	    public string Name;
	    /** the name of the arena, minus any trailing digits.
	     * the basename is used in many places to allow easy sharing of
	     * settings and things among copies of one basic arena. */
	    public string BaseName;
	    /** a handle to the main config file for this arena */
	    public ConfigHandle Cfg;
	    /** the frequency for spectators in this arena.
	     * this setting is so commonly used, it deserves a spot here. */
	    public int SpecFreq;
	    /** how many players are in ships in this arena.
	     * call GetPopulationSummary to update this. */
	    public int Playing;
	    /** how many players total are in this arena.
	     * call GetPopulationSummary to update this. */
	    public int Total;
	    /** space for private data associated with this arena */
        Dictionary<int, object> _arenaExtraData = new Dictionary<int,object>();

        public Arena(ModuleManager mm)
        {
            _mm = mm;
        }

        /// <summary>
        /// Per Arena Data
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public object this[int key]
        {
            get { return _arenaExtraData[key]; }
            set { _arenaExtraData[key] = value; }
        }

        /// <summary>
        /// This will tell you if an arena is considered a "public" arena.
        /// </summary>
        public bool IsPublic
        {
            get { return String.Compare(BaseName, PUBLIC) == 0; }
        }

        public void RemovePerArenaData(int key)
        {
            _arenaExtraData.Remove(key);
        }

        #region Overriden ComponentBroker Methods

        public override TInterface GetInterface<TInterface>()
        {
            TInterface theInterface = base.GetInterface<TInterface>();
            if(theInterface == null)
                theInterface = _mm.GetInterface<TInterface>();

            return theInterface;
        }

        public override IModuleInterface GetInterface(Type interfaceType)
        {
            IModuleInterface moduleInterface = base.GetInterface(interfaceType);
            if(moduleInterface == null)
                moduleInterface = _mm.GetInterface(interfaceType);

            return moduleInterface;
        }

        public override void DoCallbacks(string callbackIdentifier, params object[] args)
        {
            base.DoCallbacks(callbackIdentifier, args);
            _mm.DoCallbacks(callbackIdentifier, args);
        }

        #endregion
    }
}

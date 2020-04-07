using System;
using System.Collections.Generic;
using System.Text;

namespace SS.Core
{
    /// <summary>
    /// Modules that are capable of attaching to an arena implement this interface.
    /// </summary>
    public interface IArenaAttachableModule
    {
        void AttachModule(Arena arena);
        void DetachModule(Arena arena);
    }

    // TODO: looks like the arena stuff has changed somewhat dramatically with the new version...
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
        private ModuleManager _mm;

        private const string PUBLIC = "(public)";
        private const string GLOBAL = "(global)";

        private const int MAX_ARENA_NAME_LENGTH = 16;

        public const int DEFAULT_SPEC_FREQ = 8025;

        /// <summary>
        /// what state the arena is in. @see ARENA_DO_INIT, etc.
        /// </summary>
	    public ArenaState Status;
	    
        /// <summary>
        /// the full name of the arena
        /// </summary>
	    public readonly string Name;

	    /// <summary>
	    /// the name of the arena, minus any trailing digits.
        /// the basename is used in many places to allow easy sharing of
        /// settings and things among copies of one basic arena.
	    /// </summary>
        public readonly string BaseName;

	    /// <summary>
        /// a handle to the main config file for this arena
	    /// </summary>
	    public ConfigHandle Cfg;

        /// <summary>
        /// the frequency for spectators in this arena.
        /// this setting is so commonly used, it deserves a spot here.
        /// </summary>
	    public short SpecFreq;

	    /// <summary>
	    /// how many players are in ships in this arena.
        /// call GetPopulationSummary to update this.
	    /// </summary>
	    public int Playing;

	    /// <summary>
	    /// how many players total are in this arena.
        /// call GetPopulationSummary to update this.
	    /// </summary>
	    public int Total;
	    
        /// <summary>
        /// space for private data associated with this arena
        /// </summary>
        Dictionary<int, object> _arenaExtraData = new Dictionary<int,object>();

        public Arena(ModuleManager mm, string name)
        {
            _mm = mm;

            Name = name;
            BaseName = name.TrimEnd(new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' });
            if (string.IsNullOrEmpty(BaseName))
                BaseName = PUBLIC;

            Status = ArenaState.DoInit0;
            Cfg = null;
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
            object pad;
            if (_arenaExtraData.TryGetValue(key, out pad))
            {
                _arenaExtraData.Remove(key);

                IDisposable disposable = pad as IDisposable;
                if (disposable != null)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch
                    {
                        // ignore any errors
                    }
                }
            }
        }

        #region Overriden ComponentBroker Methods

        public override TInterface GetInterface<TInterface>()
        {
            // try to get the interface specific to this arena
            TInterface theInterface = base.GetInterface<TInterface>();

            if (theInterface == null)
            {
                // arena doesn't have the interface, try globally
                theInterface = _mm.GetInterface<TInterface>();
            }

            return theInterface;
        }

        public override void ReleaseInterface<TInterface>()
        {
            base.ReleaseInterface<TInterface>();

            // TODO: figure out if the interface was released here, otherwise release it on _mm
        }

        public override IComponentInterface GetInterface(Type interfaceType)
        {
            // try to get the interface specific to this arena
            IComponentInterface moduleInterface = base.GetInterface(interfaceType);

            if (moduleInterface == null)
            {
                // arena doesn't have the interface, try globally
                moduleInterface = _mm.GetInterface(interfaceType);
            }

            return moduleInterface;
        }

        public override void DoCallbacks(string callbackIdentifier, params object[] args)
        {
            // call this arena's callbacks
            base.DoCallbacks(callbackIdentifier, args);

            // call the global callbacks
            _mm.DoCallbacks(callbackIdentifier, args);
        }

        public override void DoCallback<T1>(string callbackIdentifier, T1 t1)
        {
            base.DoCallback<T1>(callbackIdentifier, t1);
            _mm.DoCallback<T1>(callbackIdentifier, t1);
        }

        public override void DoCallback<T1, T2>(string callbackIdentifier, T1 t1, T2 t2)
        {
            base.DoCallback<T1, T2>(callbackIdentifier, t1, t2);
            _mm.DoCallback<T1, T2>(callbackIdentifier, t1, t2);
        }

        public override void DoCallback<T1, T2, T3>(string callbackIdentifier, T1 t1, T2 t2, T3 t3)
        {
            base.DoCallback<T1, T2, T3>(callbackIdentifier, t1, t2, t3);
            _mm.DoCallback<T1, T2, T3>(callbackIdentifier, t1, t2, t3);
        }

        public override void DoCallback<T1, T2, T3, T4>(string callbackIdentifier, T1 t1, T2 t2, T3 t3, T4 t4)
        {
            base.DoCallback<T1, T2, T3, T4>(callbackIdentifier, t1, t2, t3, t4);
            _mm.DoCallback<T1, T2, T3, T4>(callbackIdentifier, t1, t2, t3, t4);
        }

        public override void DoCallback<T1, T2, T3, T4, T5>(string callbackIdentifier, T1 t1, T2 t2, T3 t3, T4 t4, T5 t5)
        {
            base.DoCallback<T1, T2, T3, T4, T5>(callbackIdentifier, t1, t2, t3, t4, t5);
            _mm.DoCallback<T1, T2, T3, T4, T5>(callbackIdentifier, t1, t2, t3, t4, t5);
        }

        public override void DoCallback<T1, T2, T3, T4, T5, T6>(string callbackIdentifier, T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6)
        {
            base.DoCallback<T1, T2, T3, T4, T5, T6>(callbackIdentifier, t1, t2, t3, t4, t5, t6);
            _mm.DoCallback<T1, T2, T3, T4, T5, T6>(callbackIdentifier, t1, t2, t3, t4, t5, t6);
        }

        public override void DoCallback<T1, T2, T3, T4, T5, T6, T7>(string callbackIdentifier, T1 t1, T2 t2, T3 t3, T4 t4, T5 t5, T6 t6, T7 t7)
        {
            base.DoCallback<T1, T2, T3, T4, T5, T6, T7>(callbackIdentifier, t1, t2, t3, t4, t5, t6, t7);
            _mm.DoCallback<T1, T2, T3, T4, T5, T6, T7>(callbackIdentifier, t1, t2, t3, t4, t5, t6, t7);
        }

        #endregion

        #region IArenaTarget Members

        Arena IArenaTarget.Arena
        {
            get { return this; }
        }

        #endregion

        #region ITarget Members

        TargetType ITarget.Type
        {
            get { return TargetType.Arena; }
        }

        #endregion
    }
}

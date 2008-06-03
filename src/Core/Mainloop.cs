using System;
using System.Collections.Generic;
using System.Text;
using System.Timers;
using System.Threading;

namespace SS.Core
{
    public interface IServerTimer : IComponentInterface
    {
        /// <summary>
        /// Starts a timer
        /// </summary>
        /// <typeparam name="TArg">type of the parameter the callback accepts</typeparam>
        /// <param name="callback">the method to call</param>
        /// <param name="initialDelay">how long to wait for the first call (in milliseconds)</param>
        /// <param name="interval">how long to wait between calls (in milliseconds)</param>
        /// <param name="parameter">a closure argument that will get passed to the timer callback</param>
        /// <param name="key">a key that can be used to selectively cancel timers</param>
        void SetTimer<TArg>(
            TimerDelegate<TArg> callback,
            int initialDelay,
            int interval,
            TArg parameter,
            object key);

        /// <summary>
        /// Stops and removes a timer
        /// </summary>
        /// <typeparam name="TArg">type of the parameter the callback accepts</typeparam>
        /// <param name="callback">the timer method you want to clear</param>
        /// <param name="key">timers that match this key will be removed. 
        /// using NULL means to clear all timers with the given function, regardless of key</param>
        void ClearTimer<TArg>(
            TimerDelegate<TArg> callback,
            object key);

        /// <summary>
        /// Stops and removes a timer.  The cleanupCallback will be called for each timer that was stopped, with the state object from that timer.
        /// </summary>
        /// <typeparam name="TArg">type of the parameter the callback accepts</typeparam>
        /// <param name="callback">the timer method you want to clear</param>
        /// <param name="key">timers that match this key will be removed. 
        /// using NULL means to clear all timers with the given function, regardless of key</param>
        /// <param name="cleanupCallback">cleanup a CleanupFunc to call once for each timer being cancelled</param>
        void ClearTimer<TArg>(
            TimerDelegate<TArg> callback,
            object key,
            TimerCleanupDelegate<TArg> cleanupCallback);

        /// <summary>
        /// Calls a delegate to run on a thread from the thread pool.
        /// </summary>
        /// <typeparam name="TParam">type of state object</typeparam>
        /// <param name="callback">delegate to call</param>
        /// <param name="state">state to pass to the delegate</param>
        /// <returns>true if the the delegate was successfully queued to run; otherwise false</returns>
        bool RunInThread<TParam>(WorkerDelegate<TParam> callback, TParam state);
    }

    internal interface IMainloop : IComponentInterface
    {
        /// <summary>
        /// called by the main thread which starts processing the timers
        /// </summary>
        void RunLoop();
    }

    public interface IMainloopController : IComponentInterface
    {
        /// <summary>
        /// Signals the main loop to stop
        /// </summary>
        void Quit();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TArg"></typeparam>
    /// <param name="arg"></param>
    /// <returns>true to continue timer events</returns>
    public delegate bool TimerDelegate<TArg>(TArg state);

    public delegate void TimerCleanupDelegate<TArg>(TArg state);

    public delegate void WorkerDelegate<TParam>(TParam param);

    /// <summary>
    /// The equivalent of ASSS' mainloop.[ch] but without using the main thread
    /// in a loop that processes the timers sequentially.  All the timers can run
    /// in parallel (done on thread pooled threads via the System.Timers.Timer
    /// class).  But the same timer will not fire again until after it executes 
    /// the callback.
    /// 
    /// For now, I'm keeping this clas named as Mainloop, even though it won't be 
    /// running the 'mainloop'.  I am planning on changing the name when I have
    /// all the core components working together.
    /// </summary>
    public class Mainloop : IModule, IServerTimer, IMainloop, IMainloopController
    {
        private interface ITimer : IDisposable
        {
        }

        private class MainloopTimer<TArg> : ITimer
        {
            System.Timers.Timer _timer;
            private Mainloop _owner;
            private TimerDelegate<TArg> _callback;
            private TArg _state;
            private object _key;
            private double _interval;

            // for synchronization
            private object _lockObj = new object();
            private bool _stop = false;
            private ManualResetEvent _timerExecuting = new ManualResetEvent(true);

            public MainloopTimer(
                Mainloop owner, 
                TimerDelegate<TArg> callback,
                double initialDelay, 
                double interval,
                TArg state,
                object key)
            {
                if (initialDelay <= 0)
                    throw new ArgumentOutOfRangeException("initialDelay", "must be > 0");

                if (interval < 0)
                {
                    throw new ArgumentOutOfRangeException("interval", "must be >= 0");
                }

                _owner = owner;
                _callback = callback;
                _state = state;
                _key = key;
                _interval = interval;

                _timer = new System.Timers.Timer(initialDelay);
                _timer.AutoReset = false;
                _timer.Elapsed += new ElapsedEventHandler(_timer_Elapsed);
            }

            public TimerDelegate<TArg> Callback
            {
                get { return _callback; }
            }

            public TArg State
            {
                get { return _state; }
            }

            public object Key
            {
                get { return _key; }
            }

            private void _timer_Elapsed(object sender, ElapsedEventArgs e)
            {
                bool isContinuing = false;

                lock (_lockObj)
                {
                    if (_stop)
                        return; // we've been told to stop (plus we know someone else is taking care of removing us from the timer list)

                    _timerExecuting.Reset();
                }

                try
                {
                    try
                    {
                        // execute the callback
                        bool wantsToKeepRunning = _callback(_state);

                        lock (_lockObj)
                        {
                            if (_stop || (wantsToKeepRunning == false) || (_interval <= 0))
                            {
                                return;
                            }

                            _timer.Interval = _interval;
                            _timer.Enabled = true;
                            isContinuing = true;
                        }
                    }
                    catch(Exception ex)
                    {
                        // exception means dont run again
                        Console.WriteLine("Mainloop timer caught exception: " + ex.Message);
                    }
                    finally
                    {
                        // note: releasing any threads waiting on us before trying to remove ourself from the timer list because
                        // a thread waiting for us could be holding the lock to the timer list
                        _timerExecuting.Set();
                    }
                }
                finally
                {
                    if (isContinuing == false)
                    {
                        // make sure that this timer object is removed from the timer list (it may have been removed already)
                        _owner.removeTimer(this);
                    }
                }
            }

            public void Start()
            {
                _timer.Enabled = true;
            }

            private void stop(bool waitIfExecuting)
            {
                lock (_lockObj)
                {
                    _timer.Enabled = false;
                    _stop = true;
                }

                if (waitIfExecuting)
                {
                    // block until the timer is no longer executing
                    _timerExecuting.WaitOne();

                    // there is still a chance the timer runs after this (race condition), 
                    // but at least we are certain it will not try to execute the callback because _stop == true
                }
            }

            protected virtual void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _timer.Dispose();
                }
            }

            #region IDisposable Members

            public void Dispose()
            {
                stop(true);
                Dispose(true);
            }

            #endregion

            public void Stop()
            {
                stop(false);
            }
        }

        private List<ITimer> _timerList = new List<ITimer>();
        private object _lockObj = new object();

        public Mainloop()
        {
        }

        /// <summary>
        /// used by the MainloopTimer class to remove itself
        /// </summary>
        /// <param name="timerToRemove"></param>
        private void removeTimer(ITimer timerToRemove)
        {
            bool itemRemoved;

            lock (_lockObj)
            {
                itemRemoved = _timerList.Remove(timerToRemove);
            }

            if (itemRemoved)
            {
                timerToRemove.Dispose();
            }
        }

        #region IModule Members

        Type[] IModule.InterfaceDependencies
        {
            get { return null; }
        }

        bool IModule.Load(ModuleManager mm, Dictionary<Type, IComponentInterface> interfaceDependencies)
        {
            mm.RegisterInterface<IServerTimer>(this);
            mm.RegisterInterface<IMainloop>(this);
            mm.RegisterInterface<IMainloopController>(this);
            return true;
        }

        bool IModule.Unload(ModuleManager mm)
        {
            // TODO: make sure all timers are stopped
            // technically, if all modules were written correctly all the timers would already be stopped by now

            mm.UnregisterInterface<IMainloopController>();
            mm.UnregisterInterface<IMainloop>();
            mm.UnregisterInterface<IServerTimer>();
            return true;
        }

        #endregion

        #region IServerTimer Members

        void IServerTimer.SetTimer<TArg>(
            TimerDelegate<TArg> callback, 
            int initialDelay, 
            int interval, 
            TArg state, 
            object key)
        {
            MainloopTimer<TArg> timer = new MainloopTimer<TArg>(
                this,
                callback,
                initialDelay,
                interval,
                state,
                key);

            lock (_lockObj)
            {
                _timerList.Add(timer);
            }

            timer.Start();
        }

        void IServerTimer.ClearTimer<TArg>(TimerDelegate<TArg> callback, object key)
        {
            IServerTimer st = this;
            st.ClearTimer<TArg>(callback, key, null);
        }

        void IServerTimer.ClearTimer<TArg>(
            TimerDelegate<TArg> callback,
            object key,
            TimerCleanupDelegate<TArg> cleanupCallback)
        {
            if (callback == null)
                return;

            List<MainloopTimer<TArg>> timersToDispose = new List<MainloopTimer<TArg>>();

            lock (_lockObj)
            {
                for (int x = _timerList.Count - 1; x >= 0; x--)
                {
                    MainloopTimer<TArg> timer = _timerList[x] as MainloopTimer<TArg>;
                    if(timer == null)
                        continue;

                    if ((timer.Callback == callback) &&
                        ((key == null) || (timer.Key.Equals(key))))
                    {
                        timer.Stop();

                        _timerList.RemoveAt(x);
                        timersToDispose.Add(timer);
                    }
                }
            }

            while (timersToDispose.Count > 0)
            {
                MainloopTimer<TArg> timer = timersToDispose[0];
                TArg state = timer.State;

                timer.Dispose();
                timersToDispose.RemoveAt(0);

                if (cleanupCallback != null)
                {
                    cleanupCallback(state);
                }
            }
        }

        bool IServerTimer.RunInThread<TParam>(WorkerDelegate<TParam> func, TParam param)
        {
            return ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                func(param); // avoids the cast
            });
        }

        #endregion

        #region IMainloop Members

        void IMainloop.RunLoop()
        {
            // TODO: 
        }

        #endregion

        #region IMainloopController Members

        void IMainloopController.Quit()
        {
            // TODO; 
        }

        #endregion
    }
}

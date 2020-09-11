using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Threading;

namespace SS.Core.Modules
{
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
    [CoreModuleInfo]
    public class Mainloop : IModule, IServerTimer, IMainloop, IMainloopController
    {
        #region Private Helpers

        private interface ITimer : IDisposable
        {
            void Stop();
        }

        /// <summary>
        /// Encapsulates a timer for a delegate that takes no parameters.
        /// </summary>
        private class MainloopTimer : AbstractTimer
        {
            public TimerDelegate Callback { get; }

            public MainloopTimer(
                Mainloop owner,
                TimerDelegate callback,
                int initialDelay,
                int interval,
                object key)
                : base(owner, initialDelay, interval, key)
            {
                Callback = callback;
            }

            protected override bool Execute()
            {
                return Callback();
            }
        }

        /// <summary>
        /// Encapsulates a timer for a delegate that takes a single "state" parameter.
        /// </summary>
        /// <typeparam name="TState">The type of the delegate's state parameter.</typeparam>
        private class MainloopTimer<TState> : AbstractTimer
        {
            public TimerDelegate<TState> Callback { get; }
            public TState State { get; }

            public MainloopTimer(
                Mainloop owner,
                TimerDelegate<TState> callback,
                int initialDelay,
                int interval,
                TState state,
                object key)
                : base(owner, initialDelay, interval, key)
            {
                Callback = callback;
                State = state;
            }

            protected override bool Execute()
            {
                return Callback(State);
            }
        }

        /// <summary>
        /// Base class for encapsulating timer logic.
        /// </summary>
        private abstract class AbstractTimer : ITimer
        {
            private readonly Timer _timer;
            private readonly Mainloop _owner;
            private readonly int _initialDelay;
            private readonly int _interval;
            public object Key { get; }

            // for synchronization
            private readonly object _lockObj = new object();
            private bool _stop = false;
            private readonly ManualResetEvent _timerExecuting = new ManualResetEvent(true);
            private bool _disposed = false;

            public AbstractTimer(
                Mainloop owner,
                int initialDelay,
                int interval,
                object key)
            {
                if (initialDelay <= 0)
                    throw new ArgumentOutOfRangeException("initialDelay", "must be > 0");

                if (interval <= 0 && interval != Timeout.Infinite)
                    throw new ArgumentOutOfRangeException("interval", "must be > 0 or Timeout.Infinite");

                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _initialDelay = initialDelay;
                _interval = interval;
                Key = key;

                // Creating the timer, but not starting it yet.
                _timer = new Timer(TimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
            }

            protected abstract bool Execute();

            private void TimerElapsed(object state)
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
                        bool wantsToKeepRunning = Execute();

                        lock (_lockObj)
                        {
                            if (_stop || (wantsToKeepRunning == false) || (_interval <= 0))
                            {
                                return;
                            }

                            _timer.Change(_interval, Timeout.Infinite);
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
                        _owner.RemoveTimer(this);
                    }
                }
            }

            public void Start()
            {
                _timer.Change(_initialDelay, Timeout.Infinite);
            }

            public void Stop()
            {
                Stop(false);
            }

            private void Stop(bool waitIfExecuting)
            {
                lock (_lockObj)
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
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

            #region IDisposable Members

            public void Dispose() => Dispose(true);

            protected virtual void Dispose(bool disposing)
            {
                if (_disposed)
                {
                    return;
                }

                if (disposing)
                {
                    Stop(true);
                    _timer.Dispose();
                }

                _disposed = true;
            }

            #endregion
        }

        #endregion

        private InterfaceRegistrationToken _iServerTimerToken;
        private InterfaceRegistrationToken _iMainloopToken;
        private InterfaceRegistrationToken _iMainloopController;

        private readonly List<ITimer> _timerList = new List<ITimer>();
        private readonly object _lockObj = new object();

        /// <summary>
        /// Used by <see cref="AbstractTimer"/> to remove itself.
        /// </summary>
        /// <param name="timerToRemove"></param>
        private void RemoveTimer(ITimer timerToRemove)
        {
            if (timerToRemove == null)
                throw new ArgumentNullException(nameof(timerToRemove));

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

        public bool Load(ComponentBroker broker)
        {
            _iServerTimerToken = broker.RegisterInterface<IServerTimer>(this);
            _iMainloopToken = broker.RegisterInterface<IMainloop>(this);
            _iMainloopController = broker.RegisterInterface<IMainloopController>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            // Make sure all timers are stopped.
            // There shouldn't be any left if all modules were correctly written to stop their timers when unloading.
            lock (_lockObj)
            {
                foreach (var timer in _timerList)
                {
                    timer.Stop();
                }

                foreach (var timer in _timerList)
                {
                    timer.Dispose();
                }

                _timerList.Clear();
            }

            if (broker.UnregisterInterface<IMainloopController>(ref _iMainloopController) != 0)
                return false;

            if (broker.UnregisterInterface<IMainloop>(ref _iMainloopToken) != 0)
                return false;

            if (broker.UnregisterInterface<IServerTimer>(ref _iServerTimerToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IServerTimer Members

        void IServerTimer.SetTimer(
            TimerDelegate callback,
            int initialDelay,
            int interval,
            object key)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            MainloopTimer timer = new MainloopTimer(
                this,
                callback,
                initialDelay,
                interval,
                key);

            lock (_lockObj)
            {
                _timerList.Add(timer);
            }

            timer.Start();
        }

        void IServerTimer.SetTimer<TState>(
            TimerDelegate<TState> callback, 
            int initialDelay, 
            int interval, 
            TState state, 
            object key)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            MainloopTimer<TState> timer = new MainloopTimer<TState>(
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

        void IServerTimer.ClearTimer(
            TimerDelegate callback,
            object key)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            LinkedList<MainloopTimer> timersToDispose = null;

            lock (_lockObj)
            {
                for (int x = _timerList.Count - 1; x >= 0; x--)
                {
                    if (!(_timerList[x] is MainloopTimer timer))
                        continue;

                    if ((timer.Callback == callback)
                        && ((key == null) || (key == timer.Key)))
                    {
                        timer.Stop();

                        _timerList.RemoveAt(x);

                        if (timersToDispose == null)
                            timersToDispose = new LinkedList<MainloopTimer>();

                        timersToDispose.AddLast(timer);
                    }
                }
            }

            if (timersToDispose != null)
            {
                foreach (var timer in timersToDispose)
                {
                    timer.Dispose();
                }

                timersToDispose.Clear();
            }
        }

        void IServerTimer.ClearTimer<TState>(TimerDelegate<TState> callback, object key)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            IServerTimer st = this;
            st.ClearTimer(callback, key, null);
        }

        void IServerTimer.ClearTimer<TState>(
            TimerDelegate<TState> callback,
            object key,
            TimerCleanupDelegate<TState> cleanupCallback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            LinkedList<MainloopTimer<TState>> timersToDispose = null;

            lock (_lockObj)
            {
                for (int x = _timerList.Count - 1; x >= 0; x--)
                {
                    if (!(_timerList[x] is MainloopTimer<TState> timer))
                        continue;

                    if ((timer.Callback == callback)
                        && ((key == null) || (key == timer.Key)))
                    {
                        timer.Stop();

                        _timerList.RemoveAt(x);

                        if (timersToDispose == null)
                            timersToDispose = new LinkedList<MainloopTimer<TState>>();

                        timersToDispose.AddLast(timer);
                    }
                }
            }

            if (timersToDispose != null)
            {
                foreach (var timer in timersToDispose)
                {
                    timer.Dispose();
                }

                foreach (var timer in timersToDispose)
                {
                    cleanupCallback?.Invoke(timer.State);
                }

                timersToDispose.Clear();
            }
        }

        bool IServerTimer.RunInThread<TParam>(WorkerDelegate<TParam> func, TParam param)
        {
            return ThreadPool.QueueUserWorkItem(
                delegate
                {
                    func(param);
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

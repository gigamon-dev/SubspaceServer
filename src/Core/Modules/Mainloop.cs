using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// The equivalent of ASSS' mainloop.[ch]
    /// 
    /// </summary>
    [CoreModuleInfo]
    public class Mainloop : IModule, IMainloop, IMainloopTimer, IServerTimer, IDisposable
    {
        private ComponentBroker _broker;
        private InterfaceRegistrationToken _iMainloopToken;
        private InterfaceRegistrationToken _iMainloopTimerToken;
        private InterfaceRegistrationToken _iServerTimerToken;

        // for main loop
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly CancellationToken _cancellationToken;
        private Thread _mainThread;
        private int _quitCode = 0;

        // for main loop workitems
        private readonly BlockingCollection<IRunInMainWorkItem> _runInMainQueue = new BlockingCollection<IRunInMainWorkItem>(); // TODO: maybe we should use bounding?
        private readonly AutoResetEvent _runInMainAutoResetEvent = new AutoResetEvent(false);

        // for IMainloopTimer
        private readonly LinkedList<MainloopTimer> _mainloopTimerList = new LinkedList<MainloopTimer>();
        private readonly AutoResetEvent _mainloopTimerAutoResetEvent = new AutoResetEvent(false);

        // for IServerTimer
        private readonly LinkedList<ThreadPoolTimer> _serverTimerList = new LinkedList<ThreadPoolTimer>();
        private readonly object _serverTimerLock = new object();

        public Mainloop()
        {
            _cancellationToken = _cancellationTokenSource.Token;
        }

        #region IModule Members

        public bool Load(ComponentBroker broker)
        {
            _broker = broker;
            _iMainloopTimerToken = broker.RegisterInterface<IMainloopTimer>(this);
            _iServerTimerToken = broker.RegisterInterface<IServerTimer>(this);
            _iMainloopToken = broker.RegisterInterface<IMainloop>(this);
            return true;
        }

        bool IModule.Unload(ComponentBroker broker)
        {
            // Make sure all timers are stopped.
            // There shouldn't be any left if all modules were correctly written to stop their timers when unloading.
            lock (_serverTimerLock)
            {
                if (_serverTimerList.Count > 0)
                {
                    foreach (var timer in _serverTimerList)
                    {
                        timer.Stop();
                    }

                    foreach (var timer in _serverTimerList)
                    {
                        timer.Dispose();
                    }

                    _serverTimerList.Clear();
                }
            }

            if (broker.UnregisterInterface<IMainloop>(ref _iMainloopToken) != 0)
                return false;

            if (broker.UnregisterInterface<IServerTimer>(ref _iServerTimerToken) != 0)
                return false;

            if (broker.UnregisterInterface<IMainloopTimer>(ref _iMainloopTimerToken) != 0)
                return false;

            return true;
        }

        #endregion

        #region IMainloop Members

        int IMainloop.RunLoop()
        {
            _mainThread = Thread.CurrentThread;
            _mainThread.Name = "mainloop";

            WaitHandle[] waitHandles = new WaitHandle[]
            {
                _cancellationToken.WaitHandle,
                _runInMainAutoResetEvent, 
                _mainloopTimerAutoResetEvent
            };

            while (!_cancellationToken.IsCancellationRequested)
            {
                MainloopCallback.Fire(_broker);

                // TODO: wait until the next timer needs to be processed
                // perhaps keep track of the one, so we can immediately process it
                TimeSpan waitTime = TimeSpan.FromMilliseconds(10);

                // wait until:
                // the next timer needs to be run 
                // OR 
                // we're signaled (to quit, that another timer was added, or that another work item was added)
                int waitHandleIndex = WaitHandle.WaitAny(waitHandles, waitTime);
                switch(waitHandleIndex)
                {
                    case WaitHandle.WaitTimeout:
                        // process timers, that's why we timed out
                        break;

                    case 0:
                        // we're being told to quit
                        continue;

                    case 1:
                        // at least one workitem was added
                        DrainRunInMain();
                        break;

                    case 2:
                        // at least one timer was added
                        break;
                }
            }

            return _quitCode & 0xFF;
        }

        private void DrainRunInMain()
        {
            while (_runInMainQueue.TryTake(out IRunInMainWorkItem workItem))
            {
                if (workItem != null)
                    workItem.Process();
            }
        }

        void IMainloop.Quit(ExitCode code)
        {
            _quitCode = 0x100 | (byte)code;
            _cancellationTokenSource.Cancel();
        }

        bool IMainloop.QueueMainWorkItem<TParam>(WorkerDelegate<TParam> func, TParam param)
        {
            bool ret = _runInMainQueue.TryAdd(new RunInMainWorkItem(new WorkerCallbackInvoker<TParam>(func, param)));
            _runInMainAutoResetEvent.Set();
            return ret;
        }

        void IMainloop.WaitForMainWorkItemDrain()
        {
            if (Thread.CurrentThread == _mainThread)
            {
                DrainRunInMain();
            }
            else
            {
                WaitForMainWorkItem.CreateAndWait(_runInMainQueue, _runInMainAutoResetEvent);
            }
        }

        bool IMainloop.QueueThreadPoolWorkItem<TParam>(WorkerDelegate<TParam> func, TParam param)
        {
            return ThreadPool.QueueUserWorkItem(
                delegate
                {
                    func(param);
                });
        }

        #endregion

        #region IMainloopTimer Members

        void IMainloopTimer.SetTimer(TimerDelegate callback, int initialDelay, int interval, object key)
        {
            MainloopTimer_SetTimer(new TimerCallbackInvoker(callback), initialDelay, interval, key);
        }

        void IMainloopTimer.SetTimer<TState>(TimerDelegate<TState> callback, int initialDelay, int interval, TState state, object key)
        {
            MainloopTimer_SetTimer(new TimerCallbackInvoker<TState>(callback, state), initialDelay, interval, key);
        }

        private void MainloopTimer_SetTimer(
            ITimerCallbackInvoker callbackInvoker,
            int initialDelay,
            int interval,
            object key)
        {
            if (callbackInvoker == null)
                throw new ArgumentNullException(nameof(callbackInvoker));

            // TODO: 
            throw new NotImplementedException();
        }

        void IMainloopTimer.ClearTimer(TimerDelegate callback, object key)
        {
            // TODO: 
            //MainloopTimer_ClearTimer(
            throw new NotImplementedException();
        }

        void IMainloopTimer.ClearTimer(TimerDelegate callback, object key, TimerCleanupDelegate cleanupCallback)
        {
            // TODO: 
            //MainloopTimer_ClearTimer(
            throw new NotImplementedException();
        }

        void IMainloopTimer.ClearTimer<TState>(TimerDelegate<TState> callback, object key)
        {
            // TODO: 
            //MainloopTimer_ClearTimer(
            throw new NotImplementedException();
        }

        void IMainloopTimer.ClearTimer<TState>(TimerDelegate<TState> callback, object key, TimerCleanupDelegate<TState> cleanupCallback)
        {
            // TODO: 
            //MainloopTimer_ClearTimer(
            throw new NotImplementedException();
        }

        private void MainloopTimer_ClearTimer(Delegate callback, object key, Action<MainloopTimer> timerDisposedAction)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            // TODO: 
            throw new NotImplementedException();
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

            ServerTimer_SetTimer(
                new TimerCallbackInvoker(callback),
                initialDelay,
                interval,
                key);
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

            ServerTimer_SetTimer(
                new TimerCallbackInvoker<TState>(callback, state),
                initialDelay,
                interval,
                key);
        }

        private void ServerTimer_SetTimer(
            ITimerCallbackInvoker callbackInvoker,
            int initialDelay,
            int interval,
            object key)
        {
            if (callbackInvoker == null)
                throw new ArgumentNullException(nameof(callbackInvoker));

            ThreadPoolTimer timer = new ThreadPoolTimer(
                this,
                initialDelay,
                interval,
                key,
                callbackInvoker);

            lock (_serverTimerLock)
            {
                _serverTimerList.AddLast(timer);
            }

            timer.Start();
        }

        void IServerTimer.ClearTimer(
            TimerDelegate callback,
            object key)
        {
            ServerTimer_ClearTimer(callback, key, null);
        }

        void IServerTimer.ClearTimer(
            TimerDelegate callback,
            object key,
            TimerCleanupDelegate cleanupCallback)
        {
            ServerTimer_ClearTimer(callback, key, _ => cleanupCallback());
        }

        void IServerTimer.ClearTimer<TState>(TimerDelegate<TState> callback, object key)
        {
            ServerTimer_ClearTimer(callback, key, null);
        }

        void IServerTimer.ClearTimer<TState>(
            TimerDelegate<TState> callback,
            object key,
            TimerCleanupDelegate<TState> cleanupCallback)
        {
            ServerTimer_ClearTimer(callback, key,
                (timer) =>
                {
                    if (timer.CallbackInvoker is TimerCallbackInvoker<TState> callbackInvoker)
                    {
                        cleanupCallback?.Invoke(callbackInvoker.State);
                    }
                });
        }

        private void ServerTimer_ClearTimer(Delegate callback, object key, Action<ThreadPoolTimer> timerDisposedAction)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            LinkedList<ThreadPoolTimer> timersToDispose = null;

            lock (_serverTimerLock)
            {
                LinkedListNode<ThreadPoolTimer> node = _serverTimerList.First;
                while (node != null)
                {
                    LinkedListNode<ThreadPoolTimer> next = node.Next;
                    ThreadPoolTimer timer = node.Value;

                    if (timer.CallbackInvoker.Callback.Equals(callback)
                        && ((key == null) || (key == timer.Key)))
                    {
                        timer.Stop();

                        _serverTimerList.Remove(node);

                        if (timersToDispose == null)
                            timersToDispose = new LinkedList<ThreadPoolTimer>();

                        timersToDispose.AddLast(node);
                    }

                    node = next;
                }
            }

            if (timersToDispose != null)
            {
                foreach (var timer in timersToDispose)
                {
                    timer.Dispose();
                }

                if (timerDisposedAction != null)
                {
                    foreach (var timer in timersToDispose)
                    {
                        timerDisposedAction(timer);
                    }
                }

                timersToDispose.Clear();
            }
        }

        /// <summary>
        /// Used by <see cref="ThreadPoolTimer"/> to remove itself.
        /// </summary>
        /// <param name="timerToRemove"></param>
        private void RemoveTimer(ThreadPoolTimer timerToRemove)
        {
            if (timerToRemove == null)
                throw new ArgumentNullException(nameof(timerToRemove));

            bool itemRemoved;

            lock (_serverTimerLock)
            {
                itemRemoved = _serverTimerList.Remove(timerToRemove);
            }

            if (itemRemoved)
            {
                timerToRemove.Dispose();
            }
        }

        #endregion

        public void Dispose()
        {
            _cancellationTokenSource.Dispose();
            _runInMainAutoResetEvent.Dispose();
            _mainloopTimerAutoResetEvent.Dispose();
            _runInMainQueue.Dispose();
        }

        #region Private Helpers

        private interface IRunInMainWorkItem
        {
            void Process();
        }

        private class WaitForMainWorkItem : IRunInMainWorkItem
        {
            private bool waitResolved;
            private object lockObj = new object();

            private WaitForMainWorkItem()
            {
            }

            public void Process()
            {
                lock (lockObj)
                {
                    waitResolved = true;
                    Monitor.Pulse(lockObj);
                }
            }

            // private constructor and this method forces whoever creates it, to wait on it.  So hopefully no misuse.
            public static void CreateAndWait(BlockingCollection<IRunInMainWorkItem> runInMainQueue, AutoResetEvent runInMainAutoResetEvent)
            {
                if (runInMainQueue == null)
                    throw new ArgumentNullException(nameof(runInMainQueue));

                if (runInMainAutoResetEvent == null)
                    throw new ArgumentNullException(nameof(runInMainAutoResetEvent));

                WaitForMainWorkItem workItem = new WaitForMainWorkItem();
                workItem.Wait(runInMainQueue, runInMainAutoResetEvent);
            }

            private void Wait(BlockingCollection<IRunInMainWorkItem> runInMainQueue, AutoResetEvent runInMainAutoResetEvent)
            {
                if (runInMainQueue == null)
                    throw new ArgumentNullException(nameof(runInMainQueue));

                if (runInMainAutoResetEvent == null)
                    throw new ArgumentNullException(nameof(runInMainAutoResetEvent));

                lock (lockObj)
                {
                    waitResolved = false;

                    runInMainQueue.Add(this);
                    runInMainAutoResetEvent.Set();

                    while (!waitResolved)
                    {
                        Monitor.Wait(lockObj);
                    }
                }
            }
        }

        private class RunInMainWorkItem : IRunInMainWorkItem
        {
            private IWorkerCallbackInvoker _workInvoker;

            public RunInMainWorkItem(IWorkerCallbackInvoker workInvoker)
            {
                _workInvoker = workInvoker;
            }

            public void Process()
            {
                _workInvoker.Invoke();
            }
        }

        private interface IWorkerCallbackInvoker
        {
            void Invoke();
        }

        private class WorkerCallbackInvoker<TState> : IWorkerCallbackInvoker
        {
            private readonly WorkerDelegate<TState> _callback;
            private TState _state;

            public WorkerCallbackInvoker(WorkerDelegate<TState> callback, TState state)
            {
                _callback = callback;
                _state = state;
            }

            public void Invoke()
            {
                _callback(_state);
            }
        }

        private interface ITimerCallbackInvoker
        {
            bool Invoke();

            Delegate Callback { get; }
        }

        private class TimerCallbackInvoker : ITimerCallbackInvoker
        {
            private readonly TimerDelegate _callback;

            public TimerCallbackInvoker(TimerDelegate callback)
            {
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
            }

            public Delegate Callback => _callback;

            public bool Invoke()
            {
                return _callback();
            }
        }

        private class TimerCallbackInvoker<TState> : ITimerCallbackInvoker
        {
            private readonly TimerDelegate<TState> _callback;

            public TimerCallbackInvoker(TimerDelegate<TState> callback, TState state)
            {
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
                State = state;
            }

            public Delegate Callback => _callback;

            public TState State { get; }

            public bool Invoke()
            {
                return _callback(State);
            }
        }

        public class MainloopTimer
        {
            // TODO: 
        }

        /// <summary>
        /// Encapsulates logic for a timer that runs on the thread pool.
        /// </summary>
        private class ThreadPoolTimer
        {
            private readonly Timer _timer;
            private readonly Mainloop _owner;
            private readonly int _initialDelay;
            private readonly int _interval;
            public object Key { get; }
            public ITimerCallbackInvoker CallbackInvoker;

            // for synchronization
            private readonly object _lockObj = new object();
            private bool _stop = false;
            private readonly ManualResetEvent _timerExecuting = new ManualResetEvent(true);
            private bool _disposed = false;

            public ThreadPoolTimer(
                Mainloop owner,
                int initialDelay,
                int interval,
                object key,
                ITimerCallbackInvoker callbackInvoker)
            {
                if (initialDelay <= 0)
                    throw new ArgumentOutOfRangeException("initialDelay", "must be > 0");

                if (interval <= 0 && interval != Timeout.Infinite)
                    throw new ArgumentOutOfRangeException("interval", "must be > 0 or Timeout.Infinite");

                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _initialDelay = initialDelay;
                _interval = interval;
                Key = key;
                CallbackInvoker = callbackInvoker ?? throw new ArgumentNullException(nameof(callbackInvoker));

                // Creating the timer, but not starting it yet.
                _timer = new Timer(TimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
            }

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
                        bool wantsToKeepRunning = CallbackInvoker.Invoke();

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
                    catch (Exception ex)
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
    }
}
